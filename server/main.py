import asyncio
import json
import base64
import cv2
import numpy as np
import requests
from datetime import datetime
import logging
import os
import time
import math
from typing import List, Dict, Tuple, Optional, Set
from dataclasses import dataclass, asdict
import threading
import re
import queue
import pyaudio
import wave
import websockets  # 仍然保留，用于 serve 等
from websockets.server import serve, WebSocketServerProtocol
from websockets.exceptions import ConnectionClosed, ConnectionClosedError, ConnectionClosedOK
import openai
from enum import Enum
import subprocess
import tempfile
from collections import OrderedDict 
from aiohttp import web  # 新增：HTTP 服务（异步，不阻塞主循环）
import uuid  
from aiohttp.abc import AbstractAccessLogger
import aiohttp
from packaging.version import parse as V

if V(aiohttp.__version__) >= V("3.9.0"):
    from aiohttp.web_log import AccessLogger as DefaultAccessLogger  # type: ignore[reportUnknownVariableType]
else:
    from aiohttp.helpers import AccessLogger as DefaultAccessLogger  # type: ignore[reportUnknownVariableType]
from cv2.typing import TermCriteria



class FilteredAccessLogger(AbstractAccessLogger):
    # 想静音哪些路径就写在这里（只看 path，不受 ?t=... 影响）
    QUIET_PATHS = {
        "/last_frame.jpg",
        "/last_annotated.jpg",
        "/status",
        "/panel",
        "/preview",
        } # 如需也静音 /preview，再加一个 "/preview"

    def __init__(self, logger, formatter):
        super().__init__(logger, formatter)
        # 复用官方默认实现，保证日志格式一致
        self._delegate = DefaultAccessLogger(logger, formatter)

    def log(self, request, response, time):
        if request.rel_url.path in self.QUIET_PATHS:
            return  # 这些路径不打 access 日志
        self._delegate.log(request, response, time)




logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
last_image_base64 = ""
logger = logging.getLogger(__name__)
pending_meta: "OrderedDict[str, Dict]" = OrderedDict()
# 预览缓存：最新原始帧与最新标注帧（JPEG二进制）
latest_frame_jpeg: Optional[bytes] = None
latest_annotated_jpeg: Optional[bytes] = None


# 配置常量
CONFIG = {
    'NAVER_OCR_URL': os.environ.get('NAVER_OCR_URL', ''),
    'NAVER_SECRET_KEY': os.environ.get('NAVER_SECRET_KEY', ''),
    'DETECTION_COOLDOWN': float(os.environ.get('DETECTION_COOLDOWN', '0')),
    'OCR_MIN_CONFIDENCE': float(os.environ.get('OCR_MIN_CONFIDENCE', '0.7')),
    'STREAM_URL': os.environ.get('STREAM_URL', "rtsp://192.168.0.28:8554/live"),
    'RECONNECT_DELAY': int(os.environ.get('RECONNECT_DELAY', '3')),
    'STREAM_TIMEOUT': int(os.environ.get('STREAM_TIMEOUT', '20')),
    'OPENAI_STT_MODEL': os.environ.get('OPENAI_STT_MODEL', 'whisper-1'),
    'OPENAI_API_KEY': os.environ.get('OPENAI_API_KEY', ''),
    'AUDIO_SAMPLE_RATE': 16000,
    'AUDIO_CHUNK_SIZE': 1024,
    'VOICE_ACTIVATION_THRESHOLD': 500,
    'AUDIO_RETRY_COUNT': 3,
    # 新增推送模式配置
    'USE_PUSHED_FRAMES': os.environ.get('USE_PUSHED_FRAMES', 'True').lower() == 'true',
    'RTSP_OUTPUT_URL': os.environ.get('RTSP_OUTPUT_URL', 'rtsp://0.0.0.0:8554/unity'),
    'FRAME_QUEUE_SIZE': int(os.environ.get('FRAME_QUEUE_SIZE', '30')),
    'OCR_MIN_INTERVAL': float(os.environ.get('OCR_MIN_INTERVAL', '0.6')),  # OCR最小间隔时间
    'MAX_PENDING_FRAMES': int(os.environ.get('MAX_PENDING_FRAMES', '2')),  # 最大待处理帧数
    'USE_PHONE_VOICE': os.environ.get('USE_PHONE_VOICE', 'True').lower() == 'true',
    # 是否严格要求“先说开始再找物品”。默认 False（直接说“找X”也会自动开始）
    'STRICT_START_REQUIRED':os.environ.get('STRICT_START_REQUIRED', 'False').lower() == 'true',
    'STALE_FRAME_MAX_AGE': float(os.environ.get('STALE_FRAME_MAX_AGE', '10.0')),  # 丢弃超过10秒的旧元信息
    'FRAME_MATCH_MAX_DELTA': float(os.environ.get('FRAME_MATCH_MAX_DELTA', '0.30')),  # 帧与meta配对最大时间差(秒)
    'OCR_CENTER_Y_OFFSET': int(os.environ.get('OCR_CENTER_Y_OFFSET', '15')),
    # 开启 ROI 级 OCR 回退
    'USE_OBJECT_CROP_FALLBACK': os.environ.get('USE_OBJECT_CROP_FALLBACK', 'True').lower() == 'true',
    # ROI 的放大倍数（相对 Unity 发来的 w、h；w/h 没给时用帧的短边）
    'OCR_ROI_SCALE': float(os.environ.get('OCR_ROI_SCALE', '1.8')),
    # ROI 最小、最大尺寸（避免太小或太大）
    'OCR_ROI_MIN': int(os.environ.get('OCR_ROI_MIN', '320')),
    'OCR_ROI_MAX': int(os.environ.get('OCR_ROI_MAX', '960')),
    # 每帧最多试几个物体 ROI
    'MAX_OBJECTS_PER_OCR': int(os.environ.get('MAX_OBJECTS_PER_OCR', '5')),
    'SEND_HOLDOFF_SEC': float(os.environ.get('SEND_HOLDOFF_SEC', '0.0')),  # 门控
    'OCR_OBJECT_MAX_DISTANCE': float(os.environ.get('OCR_OBJECT_MAX_DISTANCE', '60')), # OCR中心到物体中心的最大允许距离
    # === ID匹配优化配置 ===
    'MAX_CANDIDATE_RADIUS': int(os.environ.get('MAX_CANDIDATE_RADIUS', '60'))  # 候选物体搜索半径(像素)
}


# ===============================================================
# [新增] 确认模式相关变量（简化版）
# ===============================================================
CONFIRMATION_MODE_ACTIVE = False  # 确认模式激活标志
CONFIRMATION_TARGET_KEYWORD = None  # 要确认的目标关键词
CONFIRMATION_FOUND_KEYWORD = None  # 检测到的关键词（None表示未找到）
CONFIRMATION_LOCK = threading.Lock()  # 线程安全


def set_confirmation_found(keyword: str):
    """OCR检测到keyword时调用"""
    global CONFIRMATION_FOUND_KEYWORD
    with CONFIRMATION_LOCK:
        CONFIRMATION_FOUND_KEYWORD = keyword
        logger.info(f"✅ 确认模式：检测到 '{keyword}'")



class SendGate:
    """
    命中预热门：首次命中仅计数不发送；达到 warmup_hits+1 次后开始放行。
    例如 warmup_hits=1 => 丢弃第1次命中，从第2次命中开始发送。
    """
    def __init__(self, warmup_hits: int = 1):
        self.warmup_hits = max(0, int(warmup_hits))
        self.hit_count = 0
        self.open = False

    def reset(self):
        self.hit_count = 0
        self.open = False

    from typing import Optional
    def on_hit(self, now: Optional[float] = None) -> bool:
        if now is None:
            now = time.time()

        """
        每次有“命中（要发送给HoloLens/手机）”时调用。
        返回 True 才允许发送；False 表示仍在预热计数阶段。
        """
        if self.open:
            return True
        self.hit_count += 1
        if self.hit_count > self.warmup_hits:
            self.open = True
            return True
        return False





# 系统状态枚举
class SystemState(Enum):
    STANDBY = "standby"    # 待机（Unity控制）
    READY = "ready"        # 就绪（等待"시작"）
    ACTIVE = "active"      # 激活（识别中）
    PAUSE = "pause"        # 暂停（等待"시작"）

# 状态转换规则
STATE_TRANSITIONS = {
    SystemState.STANDBY: {
        'start_search': SystemState.ACTIVE,
    },
    SystemState.READY: {
        'voice_start': SystemState.ACTIVE,
        'stop_search': SystemState.STANDBY,
    },
    SystemState.ACTIVE: {
        'voice_stop': SystemState.PAUSE,
        'stop_search': SystemState.STANDBY,
    },
    SystemState.PAUSE: {
        'voice_start': SystemState.ACTIVE,
        'stop_search': SystemState.STANDBY,
    }
}

# 关键词映射（韩语命令 -> 产品名）
KEYWORD_MAPPING = {
    '비빔면': '비빔면',
    '피빔면': '비빔면',
    '피피면': '비빔면',
    '비피면': '비빔면',
    '비핌면': '비빔면',

    '삼양라면': '삼양라면',
    '삼량라뇽': '삼양라면',
    '쌈면라면': '삼양라면',
    '삼양나묘': '삼양라면',

    '사브레': '사브레',
    '사부레': '사브레',
    '사부렛': '사브레',

    '땅콩샌드': '땅콩샌드',
    '탐콩스윙의': '땅콩샌드',

    '미트볼': '미트볼',
    '미터풀': '미트볼',
    '니트볼': '미트볼',
    '미탁볼': '미트볼',

    '버터링': '버터링',
    '오토링': '버터링',
    '뽀또링': '버터링',
    '포토리': '버터링',
    '포털링': '버터링',
    '포토링': '버터링',
}

@dataclass
class DetectionResult:
    """OCR 文本中的关键词检测结果"""
    keyword: str
    text: str
    vertices: List[List[int]]
    confidence: float
    timestamp: float
    # 新增：像素坐标信息
    pixel_x: Optional[int] = None
    pixel_y: Optional[int] = None
    width: Optional[int] = None
    height: Optional[int] = None
    object_id: Optional[int] = None

@dataclass
class VoiceCommand:
    """语音命令结果"""
    raw_text: str
    keyword: str
    timestamp: float
    command_type: str

# 新增：物体追踪器
class ObjectTracker:
    """简单的物体位置追踪器"""
    def __init__(self, distance_threshold=100):
        self.tracked_objects = {}  # object_id -> last_position
        self.distance_threshold = distance_threshold
        
    def update_position(self, object_id: int, x: int, y: int):
        """更新物体位置"""
        old_pos = self.tracked_objects.get(object_id)
        self.tracked_objects[object_id] = (x, y, time.time())
        
        if old_pos:
            dx = x - old_pos[0]
            dy = y - old_pos[1]
            distance = (dx**2 + dy**2)**0.5
            return distance
        return 0
        
    def get_nearby_objects(self, x: int, y: int, radius: int = 150):
        """获取附近的物体"""
        nearby = []
        current_time = time.time()
        for obj_id, (ox, oy, timestamp) in self.tracked_objects.items():
            if current_time - timestamp > 10:  # 忽略10秒前的位置
                continue
            distance = ((x - ox)**2 + (y - oy)**2)**0.5
            if distance <= radius:
                nearby.append((obj_id, distance))
        return sorted(nearby, key=lambda x: x[1])



class VoiceInputHandler:
    """语音输入处理类"""

    def __init__(self):
        openai.api_key = CONFIG['OPENAI_API_KEY']
        
        # 事件和状态
        self.active_event = asyncio.Event()
        self.last_processing_time = 0
        self.loop = None  # 将在主线程中设置
        
        # 音频输入相关
        self.audio = None
        self.stream = None
        self.is_recording = False
        self.audio_buffer = []
        self.recording_lock = threading.Lock()

        # 命令模式
        self.start_commands = ['시작', '시작해', '시작하자', '시작해줘']
        self.stop_commands = ['종료', '멈춰', '중지', '그만', '스톱']
        
        # 搜索命令正则表达式
        self.search_patterns = [
            re.compile(r'(.+?)(?:을|를)?\s*찾아'),
            re.compile(r'(.+?)(?:을|를)?\s*검색'),
            re.compile(r'(.+?)\s*어디'),
            re.compile(r'(.+?)\s*찾기'),
            re.compile(r'(.+?)\s*주세요'),
        ]

        # PyAudio 初始化
        try:
            self.audio = pyaudio.PyAudio()
        except Exception as e:
            logger.error(f"PyAudio 初始化失败: {e}")

    def set_event_loop(self, loop):
        """设置事件循环引用"""
        self.loop = loop

    def start_recording(self):
        """开始录音"""
        with self.recording_lock:
            self.is_recording = True
            self.audio_buffer = []
            logger.info("开始录音")

    async def start_audio_stream_with_retry(self, max_retries=3):
        """启动音频流（带重试）"""
        for attempt in range(max_retries):
            if self.start_audio_stream():
                return True
            logger.warning(f"音频流启动失败，重试 {attempt + 1}/{max_retries}")
            await asyncio.sleep(1)
        return False

    def start_audio_stream(self):
        if self.audio is None:
            logger.error("PyAudio 未初始化")
            return False
        try:
            if self.stream is None:
                self.stream = self.audio.open(
                    format=pyaudio.paInt16,
                    channels=1,
                    rate=CONFIG['AUDIO_SAMPLE_RATE'],
                    input=True,
                    frames_per_buffer=CONFIG['AUDIO_CHUNK_SIZE'],
                    stream_callback=self._audio_callback
                )
                self.stream.start_stream()
                logger.info("音频流已启动")
            return True  # ← 不管是不是新开的，最终都返回 True
        except Exception as e:
            logger.error(f"音频流启动失败: {e}")
            self.stream = None
            return False


    def stop_audio_stream(self):
        """停止音频流"""
        try:
            if self.stream:
                self.stream.stop_stream()
                self.stream.close()
                self.stream = None
                logger.info("音频流已停止")
                
            # 清理录音状态
            with self.recording_lock:
                self.is_recording = False
                self.audio_buffer.clear()
                
            # 清理事件
            if self.active_event:
                self.active_event.clear()
                
        except Exception as e:
            logger.error(f"停止音频流错误: {e}")

    def _audio_callback(self, in_data, frame_count, time_info, status):
        """音频流回调函数"""
        if status:
            logger.warning(f"音频流状态: {status}")
            
        with self.recording_lock:
            if self.is_recording:
                self.audio_buffer.append(in_data)
                
        # 音量级别检测
        try:
            audio_data = np.frombuffer(in_data, dtype=np.int16)
            volume = np.abs(audio_data).mean()
            
            # 语音活动检测（检测到语音时设置事件）
            if volume > CONFIG['VOICE_ACTIVATION_THRESHOLD'] and not self.is_recording:
                self.start_recording()
                # 线程安全地设置 asyncio 事件
                if self.loop:
                    self.loop.call_soon_threadsafe(self.active_event.set)
        except Exception as e:
            logger.error(f"音频回调处理错误: {e}")
            
        return (in_data, pyaudio.paContinue)

    async def stop_recording_and_process(self):
        """停止录音并使用 Whisper API 处理"""
        tmp_path = None
        
        with self.recording_lock:
            if not self.is_recording:
                return None
            self.is_recording = False
            pcm_data = b''.join(self.audio_buffer)
            self.audio_buffer.clear()

        logger.info("停止录音，正在处理 Whisper API...")
        if not pcm_data:
            return None

        # 创建临时 WAV 文件
        try:
            with tempfile.NamedTemporaryFile(delete=False, suffix=".wav") as tmp:
                tmp_path = tmp.name
                
            with wave.open(tmp_path, 'wb') as wf:
                wf.setnchannels(1)
                wf.setsampwidth(pyaudio.get_sample_size(pyaudio.paInt16))
                wf.setframerate(CONFIG['AUDIO_SAMPLE_RATE'])
                wf.writeframes(pcm_data)

            # 调用 Whisper API
            with open(tmp_path, "rb") as audio_file:
                resp = openai.Audio.transcribe(
                    model=CONFIG['OPENAI_STT_MODEL'],
                    file=audio_file,
                    language="ko"
                )
            # 确保 resp 是字典类型再调用 get
            if isinstance(resp, dict):
                text = resp.get("text", "").strip()
            else:
                text = str(resp).strip()
            logger.info(f"Whisper 识别结果: {text}")
            return self._parse_command(text)
            
        except Exception as e:
            logger.error(f"Whisper API 调用失败: {e}")
            return None
        finally:
            if tmp_path and os.path.exists(tmp_path):
                try:
                    os.unlink(tmp_path)
                except Exception as e:
                    logger.error(f"临时文件删除失败: {e}")

    def _parse_command(self, text: str) -> Optional[VoiceCommand]:
        """解析语音命令（优先级：控制命令 > 搜索命令）"""
        if not text:
            return None
        
        # 1. 首先检查开始命令（最高优先级）
        for cmd in self.start_commands:
            if cmd in text:
                return VoiceCommand(
                    raw_text=text,
                    keyword='',
                    timestamp=time.time(),
                    command_type='start'
                )
        
        # 2. 检查停止命令（次高优先级）
        for cmd in self.stop_commands:
            if cmd in text:
                return VoiceCommand(
                    raw_text=text,
                    keyword='',
                    timestamp=time.time(),
                    command_type='stop'
                )
            
        # 3. 最后检查搜索命令
        for pattern in self.search_patterns:
            match = pattern.search(text)
            if match:
                search_term = match.group(1).strip()
                # 查找匹配的关键词
                for keyword in KEYWORD_MAPPING.keys():
                    if keyword in search_term:
                        return VoiceCommand(
                            raw_text=text,
                            keyword=keyword,
                            timestamp=time.time(),
                            command_type='search'
                        )
                logger.warning(f"未找到匹配的关键词: {search_term}")
                
        return None

    def stop(self):
        """停止音频流"""
        try:
            self.stop_audio_stream()
            if self.audio:
                self.audio.terminate()
                self.audio = None
        except Exception as e:
            logger.error(f"音频清理错误: {e}")


class NaverOCR:
    """Naver Clova OCR 客户端"""
    
    def __init__(self, api_url: str, secret_key: str):
        self.api_url = api_url
        self.secret_key = secret_key
        self.last_request_time = 0.0
        self.min_interval = 0.1
        # self._lock = threading.Lock()  <--- 已删除
        self.session = requests.Session()
        self.session.headers.update({'X-OCR-SECRET': self.secret_key})

    def __del__(self):
        if hasattr(self, 'session'):
            self.session.close()

    def recognize_text(self, image_data: bytes) -> List[Dict]: # <--- 恢复了这一行
        """从图像中识别文本"""
        # with self._lock: <--- 已删除
        
        # 下面的代码块已修正缩进
        now = time.time()
        elapsed = now - self.last_request_time
        if elapsed < self.min_interval:
            time.sleep(self.min_interval - elapsed)
        self.last_request_time = time.time()
            
        timestamp = int(datetime.now().timestamp() * 1000)
        payload = {
            'version': 'V1',
            'requestId': f"req_{timestamp}",
            'timestamp': timestamp,
            'lang': 'ko',
            'images': [{
                'format': 'jpg',
                'name': 'image',
                'data': base64.b64encode(image_data).decode('utf-8')
            }]
        }
        
        try:
            response = self.session.post(self.api_url, json=payload, timeout=5)
            response.raise_for_status()
            result = response.json()
            return self._parse_ocr_result(result)
        except Exception as e:
            logger.error(f"OCR 错误: {e}")
            return []

    def _parse_ocr_result(self, result: dict) -> List[Dict]:
        """解析 OCR 结果"""
        text_results = []
        for image in result.get('images', []):
            for field in image.get('fields', []):
                confidence = float(field.get('inferConfidence', 0) or 0)
                text = field.get('inferText', '')
                if confidence >= CONFIG['OCR_MIN_CONFIDENCE'] and text:
                    vertices = []
                    if 'boundingPoly' in field:
                        vertices = [[v['x'], v['y']] for v in field['boundingPoly']['vertices']]
                    text_results.append({
                        'text': text,
                        'vertices': vertices,
                        'confidence': confidence
                    })
        return text_results


class KeywordMatcher:
    """快速关键词匹配器"""
    
    def __init__(self):
        self.keywords = { "비빔면", "삼양라면", "사브레", "땅콩샌드", "미트볼","버터링"}
        self.partial_map = {
            "삼엄라면": "삼양라면",
            "사브": "사브레",
            "사바레": "사브레",
            "사비레": "사브레",
            "시브레": "사브레",
            "사브례": "사브레",
            "땅콩": "땅콩샌드",
            "땅킁센드": "땅콩샌드",
            "미트블": "미트볼",
            "미트불": "미트볼",
            "미토블": "미트볼",
            '상영라면': '삼양라면',
            '상양라면': '삼양라면',
            "버토링": "버터링",
            "뽀또링": "버터링",
            "포토리": "버터링",
            "버토리": "버터링",
            "버투링": "버터링",
            "버토리": "버터링",
            "비비면": "비빔면",
            "비빔": "비빔면",
            "피빔면": "비빔면",
            "피핌면": "비빔면",
            "비핌면": "비빔면",

        }
        self.normalize_pattern = re.compile(r'[^가-힣a-zA-Z0-9]')

    def find_matches(self, text: str) -> List[str]:
        """从文本中查找关键词"""
        if not text:
            return []
            
        matches = []
        normalized = self.normalize_pattern.sub('', text)
        
        # 精确关键词匹配
        for keyword in self.keywords:
            if keyword in normalized:
                matches.append(keyword)
                
        # 部分匹配检查
        if not matches:
            for partial, full_keyword in self.partial_map.items():
                if partial in normalized:
                    matches.append(full_keyword)
                    break
                    
        return matches


# WebSocket 客户端集合
connected_clients: set[WebSocketServerProtocol] = set()


async def broadcast(clients: Set[WebSocketServerProtocol], message: str):
    """将 message 广播给所有已连接的 WebSocket 客户端（异步、并发发送，移除已关闭连接）。"""
    if not clients:
        return
    coros = []
    # 使用快照以避免在迭代时修改集合
    for ws in list(clients):
        try:
            coros.append(ws.send(message))
        except Exception:
            try:
                clients.discard(ws)
            except Exception:
                pass
    if coros:
        # 并发发送，忽略单个连接错误
        await asyncio.gather(*coros, return_exceptions=True)


def _websockets_broadcast_fire_and_forget(clients: Set[WebSocketServerProtocol], message: str):
    """
    兼容性包装：把广播任务以 fire-and-forget 方式提交到当前运行的事件循环，
    供代码中直接调用 websockets.broadcast(...)（不 await）时使用。
    """
    try:
        loop = asyncio.get_event_loop()
    except RuntimeError:
        loop = None

    if loop and loop.is_running():
        loop.create_task(broadcast(clients, message))
    else:
        # 如果没有运行中的事件循环，则同步执行（很少出现的启动时场景）
        asyncio.run(broadcast(clients, message))


# 兼容：将 wrapper 绑定到 websockets 模块，以免现有代码调用 websockets.broadcast 报错
setattr(websockets, "broadcast", _websockets_broadcast_fire_and_forget)


# 全局处理器引用（用于 WebSocket 控制）
global_processor = None

# 全局 FFmpeg 进程管理器
class FFmpegManager:
    def __init__(self):
        self.process = None
        self.is_running = False
        self.lock = threading.Lock()
        
    def start(self):
        """启动 FFmpeg 进程"""
        with self.lock:
            if self.is_running:
                logger.warning("FFmpeg 已经在运行")
                return True
                
            try:
                # FFmpeg 命令参数
                ffmpeg_cmd = [
                    'C:/Users/14288/Desktop/ffmpeg-7.1.1-essentials_build/bin/ffmpeg.exe',
                    '-f', 'mjpeg',           # 输入格式为 MJPEG
                    '-i', '-',               # 从 stdin 读取
                    '-c:v', 'libx264',       # 使用 H264 编码
                    '-preset', 'ultrafast',  # 最快编码预设
                    '-tune', 'zerolatency',  # 零延迟调优
                    '-muxdelay', '0',  # 减少延迟
                    '-rtsp_transport', 'tcp',  # 优先使用TCP，更稳定
                    '-rtsp_flags', 'listen',  # 【核心】让FFmpeg作为服务器监听
                    '-f', 'rtsp',            # 输出格式为 RTSP
                    CONFIG['RTSP_OUTPUT_URL']
                ]
                
                self.process = subprocess.Popen(
                    ffmpeg_cmd,
                    stdin=subprocess.PIPE,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.PIPE,
                    bufsize=10**8
                )

                
                self.is_running = True
                logger.info(f"FFmpeg 进程已启动，输出到: {CONFIG['RTSP_OUTPUT_URL']}")
                
                # 在后台线程读取 stderr（避免缓冲区满）
                threading.Thread(target=self._read_stderr, daemon=True).start()
                
                return True
                
            except Exception as e:
                logger.error(f"FFmpeg 启动失败: {e}")
                self.is_running = False
                return False
    
    def _read_stderr(self):
        """FFmpeg stderr 로깅"""
        if not self.process:
            return

        stderr = getattr(self.process, "stderr", None)
        if stderr is None:
            logger.debug("FFmpeg process has no stderr to read")
            return

        try:
            # 使用 getattr 返回的可调用（或回退到空 lambda）以避免 NoneType.readline 报错
            read_fn = getattr(stderr, "readline", None)
            if not callable(read_fn):
                logger.debug("FFmpeg stderr has no callable readline")
                return

            for line in iter(read_fn, b''):
                if not line:
                    continue
                try:
                    # Decode only if we actually got bytes; otherwise fallback to str()
                    if isinstance(line, (bytes, bytearray)):
                        decoded = line.decode('utf-8', errors='ignore').strip()
                    else:
                        decoded = str(line).strip()
                except Exception:
                    decoded = str(line).strip()
                logger.warning(f"[FFmpeg] {decoded}")
        except Exception as e:
            logger.debug(f"读取 FFmpeg stderr 时出错: {e}")

    
    def write_frame(self, jpeg_bytes):
        with self.lock:
            def _attempt_write():
                if self.process and self.process.stdin:
                    self.process.stdin.write(jpeg_bytes)
                    self.process.stdin.flush()
                else:
                    logger.warning("[FFmpeg] stdin 不可用，丢弃一帧")

                logger.debug(f"[FFmpeg] 프레임 {len(jpeg_bytes)} 바이트 전송됨")

            if self.is_running and self.process and self.process.stdin:
                try:
                    _attempt_write()
                    return True
                except Exception as e:
                    logger.error(f"[FFmpeg] 프레임 전송 실패: {e}")
                    self.is_running = False

                    # 프로세스 복구 시도
                    logger.info("[FFmpeg] 전송 실패 → 자동 재시작 시도 중...")
                    self.stop()
                    if self.start():
                        time.sleep(0.2)
                        try:
                            _attempt_write()
                            logger.info("[FFmpeg] 프로세스 복구 및 프레임 재전송 성공")
                            return True
                        except Exception as e2:
                            logger.error(f"[FFmpeg] 재전송 실패: {e2}")
                            self.is_running = False
                            return False
                    else:
                        logger.error("[FFmpeg] 자동 재시작 실패")
                        return False
            else:
                logger.warning("[FFmpeg] write_frame: 프로세스 비정상 상태")
                return False

    
    def stop(self):
        """停止 FFmpeg 进程"""
        with self.lock:
            if self.process:
                try:
                    if self.process.stdin:
                        self.process.stdin.close()
                    self.process.terminate()
                    self.process.wait(timeout=10)
                except Exception as e:
                    logger.error(f"FFmpeg 停止错误: {e}")
                    self.process.kill()
                finally:
                    self.process = None
                    self.is_running = False
                    logger.info("FFmpeg 进程已停止")

# 全局 FFmpeg 管理器实例
ffmpeg_manager = FFmpegManager()

# 帧队列（用于 Unity 推送模式）
frame_queue = asyncio.Queue(maxsize=CONFIG['FRAME_QUEUE_SIZE'])

async def ws_handler(websocket, path):
    """WebSocket 连接处理（控制命令）"""
    connected_clients.add(websocket)
    logger.info(f"WebSocket 客户端已连接: {path}")
    
    # 发送连接成功消息
    await websocket.send(json.dumps({
        'type': 'connection',
        'status': 'connected',
        'message': '성공적으로 연결되었습니다'
    }, ensure_ascii=False))
    
    try:
        async for message in websocket:
            # 处理来自 Unity 的控制命令
            try:
                data = json.loads(message)
                logger.info(f"[命令消息] 原始内容: {data}") 
                command = data.get('command')
                logger.info(f"[命令消息] 解析到 command: {command}")

                # 1) image_meta는 오직 pending_meta에만 저장하고 루프 재진입
                if command == 'image_meta':
                    img_id = data.get('id')
                    if not img_id:
                        await websocket.send(json.dumps(
                            {'type': 'error', 'message': 'image_meta 缺少 id'}, ensure_ascii=False
                        ))
                        continue

                    ts = data.get('ts') or data.get('timestamp') or int(time.time() * 1000)

                    pending_meta[img_id] = {
                        'position': data.get('position'),
                        'rotation': data.get('rotation'),
                        'ts': ts,
                    }
                    continue


                # 2) global_processor 준비 안 된 경우 에러 전송 후 재진입
                if global_processor is None:
                    await websocket.send(json.dumps({
                        'type': 'error',
                        'message': '시스템이 아직 준비되지 않았습니다'
                    }, ensure_ascii=False))
                    continue  # ← 반드시 블록 안에!

                # 3) 나머지 명령 처리
                if command == 'start_search':
                    success = await global_processor.handle_transition('start_search')
                    if success:
                        await websocket.send(json.dumps({
                            'type': 'command_response',
                            'command': 'start_search',
                            'status': 'success',
                            'message': '검색을 시작합니다',
                            'state': global_processor.state.value
                        }, ensure_ascii=False))
                    else:
                        await websocket.send(json.dumps({
                            'type': 'command_response',
                            'command': 'start_search',
                            'status': 'invalid_transition',
                            'message': f'현재 상태({global_processor.state.value})에서는 이 명령을 수행할 수 없습니다',
                            'state': global_processor.state.value
                        }, ensure_ascii=False))

                elif command == 'stop_search':
                    success = await global_processor.handle_transition('stop_search')
                    if success:
                        await websocket.send(json.dumps({
                            'type': 'command_response',
                            'command': 'stop_search', 
                            'status': 'success',
                            'message': '대기 모드로 전환되었습니다',
                            'state': global_processor.state.value
                        }, ensure_ascii=False))
                    else:
                        await websocket.send(json.dumps({
                            'type': 'command_response',
                            'command': 'stop_search',
                            'status': 'already_standby',
                            'message': '이미 대기 모드입니다',
                            'state': global_processor.state.value
                        }, ensure_ascii=False))

                elif command == 'get_status':
                    status = {
                        'type': 'status',
                        'state': global_processor.state.value,
                        'is_searching': global_processor.current_search_target is not None,
                        'current_target': global_processor.current_search_target,
                        'has_announced_found': global_processor.has_announced_found
                    }
                    await websocket.send(json.dumps(status, ensure_ascii=False))

                elif command == 'set_keywords':
                    kws = data.get('keywords', [])
                    logger.info(f"[命令消息] 收到 set_keywords: {kws}")
                    if kws:
                        await global_processor.set_search_target(kws[0])
                        await websocket.send(json.dumps({
                            'type': 'command_response',
                            'command': 'set_keywords',
                            'status': 'success',
                            'message': f'已设置搜索关键词: {kws[0]}',
                            'state': global_processor.state.value
                        }, ensure_ascii=False))
                    else:
                        await websocket.send(json.dumps({
                            'type': 'command_response',
                            'command': 'set_keywords',
                            'status': 'error',
                            'message': '未提供 keywords 列表'
                        }, ensure_ascii=False))

                else:
                    await websocket.send(json.dumps({
                        'type': 'error',
                        'message': f'알 수 없는 명령: {command}'
                    }, ensure_ascii=False))

            except json.JSONDecodeError:
                logger.error("无效的 JSON 消息")
                await websocket.send(json.dumps({
                    'type': 'error',
                    'message': '잘못된 JSON 형식입니다'
                }, ensure_ascii=False))

                
    except ConnectionClosed:
        logger.info("WebSocket 连接正常关闭")
    except Exception as e:
        logger.error(f"WebSocket 连接错误: {e}")
    finally:
        connected_clients.discard(websocket)
        logger.info("WebSocket 客户端已断开连接")

async def frame_ws_handler(websocket, path):
    """WebSocket 帧接收处理器（接收 Unity 推送的 JPEG 帧）"""
    global latest_frame_jpeg
    logger.info(f"帧推送客户端已连接: {path}")
    frame_count = 0
    
    try:
        async for message in websocket:
            # message 应该是二进制的 JPEG 数据
            if isinstance(message, bytes):
                frame_count += 1

                logger.info(f"📥 프레임 수신 ({len(message)} bytes)")
                ffmpeg_manager.write_frame(message)
                # 缓存“最新原始帧”用于网页预览
                latest_frame_jpeg = message

                            
                # 放入处理队列（如果队列满则丢弃最旧的）
                try:
                    if frame_queue.full():
                        # 丢弃最旧的帧
                        try:
                            frame_queue.get_nowait()
                        except asyncio.QueueEmpty:
                            pass
                    
                    recv_ts = time.time()  # 服务器收到该帧的时间(秒)
                    await frame_queue.put((message, recv_ts))

                    
                    if frame_count % 30 == 0:  # 每30帧记录一次
                        logger.debug(f"已接收 {frame_count} 帧")
                        
                except Exception as e:
                    logger.error(f"帧入队错误: {e}", exc_info=True)
            else:
                logger.warning("收到非二进制消息")
                
    except ConnectionClosed:
        logger.info(f"帧推送连接已关闭，共接收 {frame_count} 帧")
    except Exception as e:
        logger.error(f"帧接收错误: {e}", exc_info=True)

async def save_frame_to_disk(image: np.ndarray, timestamp_ms: int):
    """
    백그라운드에서 디스크에 프레임 저장.
    run_in_executor 로 메인 이벤트 루프 블로킹 방지.
    """
    save_dir = "saved_frames"
    os.makedirs(save_dir, exist_ok=True)
    filename = os.path.join(save_dir, f"frame_{timestamp_ms}.jpg")
    loop = asyncio.get_running_loop()
    # cv2.imwrite 을 쓰레드 풀에서 실행
    await loop.run_in_executor(None, cv2.imwrite, filename, image)


class StreamProcessor:
    """视频流处理器"""
    @staticmethod
    def _norm_ts_to_seconds(ts):
        ts = float(ts)
        return ts / 1000.0 if ts > 1e10 else ts

    def __init__(self, stream_url: str, ocr_client: NaverOCR, voice_handler: VoiceInputHandler):
        self.stream_url = stream_url
        self.ocr = ocr_client
        self.voice = voice_handler
        self.matcher = KeywordMatcher()
        self.is_running = False
        self.last_detection_time = 0
        self.frame_count = 0
        self.detection_results = queue.Queue(maxsize=100)
        
        # 搜索相关
        self.current_search_target = None
        self.search_start_time = None
        self.search_timeout = 300  # 5分钟
        
        # 视频捕获
        self.cap = None
        self.reconnect_attempts = 0
        self.max_reconnect_attempts = 10
        
        # 系统状态
        self.state = SystemState.STANDBY
        
        # 标志：是否已经播报过"找到了"
        self.has_announced_found = False
        
        # 标志：是否已经播报过"请说要寻找的物品"
        self.has_announced_prompt = False
        
        # 推送模式标志
        self.use_pushed_frames = CONFIG['USE_PUSHED_FRAMES']

        # 帧处理控制
        self.last_ocr_time = 0  # 上次OCR处理时间
        self.pending_ocr_count = 0  # 当前正在处理的OCR数量
        self.frame_process_lock = asyncio.Lock()  # 帧处理锁
        self.skipped_frames_count = 0  # 统计跳过的帧数
        self.processed_frames_count = 0  # 统计处理的帧数
        self.fast_detection_mode = False
        self.fast_mode_counter = 0
        self.fast_detection_frames = 2   # 前2帧优先处理
        self.min_interval = 0.6         # 秒；约0.6s/帧 → ~1.5s处理两帧
        self.max_interval = 2.0          # 自适应的上限，保留你后续调节逻辑
        self.adaptive_interval = self.min_interval

        self.state_change_time = 0
        
        # 新增：物体OCR模式控制标志
        self.object_ocr_mode = False  # 是否启用基于分割的OCR模式

        # StreamProcessor.__init__(...)
        self.send_gate = SendGate(warmup_hits=int(os.environ.get('WARMUP_HITS_BEFORE_SEND', '0')))  # ✅ 实例化门控器

        
        # 新增：物体追踪器
        self.object_tracker = ObjectTracker()
        self.one_shot_found_text = None   # 仅首次找到时写入"찾았습니다."，被/status读一次后清空
        self.ocr_task_queue = asyncio.LifoQueue(maxsize=1)  # 只保留最新任务
        self.ocr_worker_task = None
        self.latest_enqueued_ts = 0.0  # 最近一次入队帧的接收时间

        self.recognition_paused = False      # 是否暂停识别
        self.lock_object_id = None           # 被锁定的 Cube/Object ID
        self.last_selected_object_id = None  # 最近一次选中的对象（由 /ocr 写入）
        self.last_ocr_hit = None             # 最近一次 OCR 命中（由 /ocr 写入）


    



    async def handle_transition(self, event: str) -> bool:
        """处理状态转换"""
        current_state = self.state
        
        # 检查转换是否合法
        if current_state not in STATE_TRANSITIONS:
            return False
            
        transitions = STATE_TRANSITIONS[current_state]
        if event not in transitions:
            logger.warning(f"无效的状态转换: {current_state} -> {event}")
            return False
            
        # 执行状态转换
        new_state = transitions[event]
        await self.on_state_changed(current_state, new_state)
        return True


    async def on_state_changed(self, old_state: SystemState, new_state: SystemState):
        """状态切换时的统一处理"""
        logger.info(f"状态切换: {old_state.value} -> {new_state.value}")
        self.state = new_state
        self.send_gate.reset()
        self.one_shot_found_text = None  # 清除一次性找到文本
        self.state_change_time = time.time()
        
        # 离开旧状态的清理
        if old_state == SystemState.ACTIVE:
            # 离开 ACTIVE 时关闭快速模式
            self.fast_detection_mode = False
            self.adaptive_interval = self.min_interval  # 恢复到最小间隔
            self.object_ocr_mode = False  # 停止物体OCR模式
            
        # 进入新状态的初始化
        if new_state == SystemState.STANDBY:
            # 进入 STANDBY：关闭所有
            self.voice.stop_audio_stream()
            self.clear_search_target()
            self.has_announced_prompt = False
            self.object_ocr_mode = False
            
        elif new_state == SystemState.READY:
            # 进入 READY：仅打开音频流
            success = await self.voice.start_audio_stream_with_retry()
            if not success:
                logger.error("无法启动音频流，回到 STANDBY")
                await self.on_state_changed(new_state, SystemState.STANDBY)
                return
                
        elif new_state == SystemState.ACTIVE:
            # 进入 ACTIVE：启用快速检测模式
            logger.info("✔✔ 系统已进入 ACTIVE 状态")
            # 不再自动启用整帧OCR的快速检测模式，等待Unity的物体OCR请求
            self.has_announced_prompt = False
            self.has_announced_found = False

            # 如果不使用手机语音，才需要打开本地麦克风
            if not CONFIG.get('USE_PHONE_VOICE', True):
                if not self.voice.stream:
                    success = await self.voice.start_audio_stream_with_retry()
                    if not success:
                        logger.error("无法启动音频流")

        elif new_state == SystemState.PAUSE:
         # 进入 PAUSE：清除当前搜索
            self.clear_search_target()
            self.object_ocr_mode = False
            
        # 广播状态变化
        if connected_clients:
            message = {
                'type': 'state_changed',
                'old_state': old_state.value,
                'new_state': new_state.value,
                'timestamp': time.time(),
                'fast_mode': self.fast_detection_mode,
                'object_ocr_mode': self.object_ocr_mode  # 新增
            }
            _websockets_broadcast_fire_and_forget(connected_clients, json.dumps(message, ensure_ascii=False))

        

    async def set_search_target(self, keyword: str):
        """设置搜索目标"""
        self.send_gate.reset()
        self.current_search_target = keyword
        self.search_start_time = time.time()
        self.has_announced_found = False
        
        # 设置新目标时启用物体OCR模式（而不是快速检测模式）
        self.object_ocr_mode = True
        self.fast_detection_mode = False  # 确保关闭整帧OCR模式
        self.one_shot_found_text = None  # 清除一次性找到文本
        logger.info(f"搜索目标已设置: {keyword}，启用物体OCR模式")
        
        # 广播搜索开始消息
        if connected_clients:
            message = {
                'type': 'search_started',
                'target': keyword,
                'target_display': KEYWORD_MAPPING.get(keyword, keyword),
                'message': f'{keyword}를 찾고 있습니다',
                'timestamp': time.time(),
                'object_ocr_mode': True  # 告知Unity使用物体OCR模式
            }
            _websockets_broadcast_fire_and_forget(connected_clients, json.dumps(message, ensure_ascii=False))
    

    def clear_search_target(self):
        """清除搜索目标"""
        self.current_search_target = None
        self.search_start_time = None
        self.has_announced_found = False
        self.object_ocr_mode = False  # 停止物体OCR模式
        self.one_shot_found_text = None # 清除一次性找到文本


    async def pause_lock(self, lock_id: Optional[str] = None):
        """暂停识别；若提供 lock_id，则锁住该对象；否则锁住最近一次选中的对象。"""
        self.recognition_paused = True
        if lock_id:
            self.lock_object_id = lock_id
        else:
            self.lock_object_id = self.last_selected_object_id
        self.send_gate.reset()  # 暂停时重置门控，避免恢复后立刻喷发
        return self.lock_object_id

    async def resume_recognition(self):
        """恢复识别，解除锁定。"""
        self.recognition_paused = False
        self.lock_object_id = None
        self.one_shot_found_text = None
        self.send_gate.reset()
        # 不改动 state；仍保持 ACTIVE/READY 等原状态

    async def reset_system(self):
        """复位到初始（等价于清空状态+回到 STANDBY），不退出服务。"""
        self.recognition_paused = False
        self.lock_object_id = None
        self.last_selected_object_id = None
        self.last_ocr_hit = None
        self.clear_search_target()
        self.send_gate.reset()
        await self.on_state_changed(self.state, SystemState.STANDBY)


    def connect_stream(self) -> bool:
        """连接视频流（仅在非推送模式下使用）"""
        if self.use_pushed_frames:
            logger.info("使用推送模式，跳过 RTSP 连接")
            return True
            
        try:
            if self.cap is not None:
                self.cap.release()
                
            # 根据流 URL 选择连接方式
            if self.stream_url.startswith('rtsp://'):
                self.cap = cv2.VideoCapture(self.stream_url, cv2.CAP_FFMPEG)
                self.cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
            elif self.stream_url.isdigit():
                # 网络摄像头索引
                self.cap = cv2.VideoCapture(int(self.stream_url))
            else:
                # 文件或其他 URL
                self.cap = cv2.VideoCapture(self.stream_url)
                
            # 设置超时
            if hasattr(cv2, 'CAP_PROP_OPEN_TIMEOUT_MSEC'):
                self.cap.set(cv2.CAP_PROP_OPEN_TIMEOUT_MSEC, CONFIG['STREAM_TIMEOUT'] * 1000)
                
            # 检查连接
            if self.cap.isOpened():
                ret, _ = self.cap.read()
                if ret:
                    logger.info(f"流连接成功: {self.stream_url}")
                    self.reconnect_attempts = 0
                    return True
                    
            logger.error("流读取失败")
            return False
            
        except Exception as e:
            logger.error(f"流连接失败: {e}")
            return False
    
    async def reconnect_stream(self) -> bool:
        """重连视频流（带延迟）"""
        if self.use_pushed_frames:
            return True
            
        self.reconnect_attempts += 1
        if self.reconnect_attempts > self.max_reconnect_attempts:
            logger.error(f"重连失败次数过多 ({self.max_reconnect_attempts})，停止尝试")
            return False
            
        logger.info(f"尝试重连... (第 {self.reconnect_attempts} 次)")
        await asyncio.sleep(CONFIG['RECONNECT_DELAY'])
        return self.connect_stream()

    async def get_frame(self) -> Optional[Tuple[np.ndarray, float]]:
        """获取下一帧（支持推送模式和拉取模式），统一返回 (frame, recv_ts)"""
        if self.use_pushed_frames:
            try:
                item = await asyncio.wait_for(frame_queue.get(), timeout=1.0)
                if isinstance(item, tuple):
                    jpeg_bytes, recv_ts = item
                else:
                    jpeg_bytes, recv_ts = item, time.time()
                np_arr = np.frombuffer(jpeg_bytes, np.uint8)
                frame = cv2.imdecode(np_arr, cv2.IMREAD_COLOR)
                return (frame, recv_ts) if frame is not None else None
            except asyncio.TimeoutError:
                return None
            except Exception as e:
                logger.error(f"帧解码错误: {e}")
                return None
        else:
            # 拉取模式：从 VideoCapture 读取
            if self.cap and self.cap.isOpened():
                ret, frame = self.cap.read()
                return (frame, time.time()) if ret else None
            return None



    async def should_process_frame(self) -> bool:
        """决定是否应该处理当前帧"""
        # 如果启用了物体OCR模式，则不处理整帧
        if self.object_ocr_mode:
            return False
            
        current_time = time.time()
        
        # 1. 快速检测模式：优先处理
        if self.fast_detection_mode:
            if self.fast_mode_counter < self.fast_detection_frames:
                # 但仍需要遵守最小OCR间隔
                if current_time - self.last_ocr_time >= self.min_interval:
                    self.fast_mode_counter += 1
                    logger.info(f"🚀 快速检测模式: 处理第 {self.fast_mode_counter}/{self.fast_detection_frames} 帧")
                    return True
                else:
                    remaining = self.min_interval - (current_time - self.last_ocr_time)
                    logger.debug(f"快速模式等待中: {remaining:.2f}s")
                    return False
            else:
                # 退出快速模式
                self.fast_detection_mode = False
                self.adaptive_interval = self.min_interval  
                logger.info("退出快速检测模式")
        
        # 2. 检查待处理数量
        if self.pending_ocr_count >= CONFIG['MAX_PENDING_FRAMES']:
            logger.debug(f"待处理OCR已满: {self.pending_ocr_count}/{CONFIG['MAX_PENDING_FRAMES']}")
            return False
        
        # 3. 基于自适应时间间隔
        if current_time - self.last_ocr_time < self.adaptive_interval:
            return False
        
        # 4. 检测后的冷却时间（仅在找到目标后）
        if self.has_announced_found and current_time - self.last_detection_time < CONFIG['DETECTION_COOLDOWN']:
            return False
        
        return True


    def process_frame(self, frame: np.ndarray) -> Optional[DetectionResult]:
        """处理帧（OCR 和关键词匹配）"""
        try:
            # 编码为 JPEG
            success, encoded_img = cv2.imencode('.jpg', frame, [cv2.IMWRITE_JPEG_QUALITY, 85])
            if not success:
                return None
                
            # 执行 OCR
            ocr_results = self.ocr.recognize_text(encoded_img.tobytes())
            if not ocr_results:
                return None
                
            all_matches: List[DetectionResult] = []
            logger.debug(f"ocr 결과: {ocr_results}")
            for result in ocr_results:
                text = result['text']
                conf = result['confidence']
                vertices = result['vertices']
                
                # 关键词匹配
                keywords = self.matcher.find_matches(text)
                
                # ===== [关键修改] 只在确认模式期间记录OCR检测 =====
                # 避免搜索模式污染缓存
                global CONFIRMATION_MODE_ACTIVE, CONFIRMATION_TARGET_KEYWORD
                if CONFIRMATION_MODE_ACTIVE and CONFIRMATION_TARGET_KEYWORD:
                    for kw in keywords:
                        normalized = KEYWORD_MAPPING.get(kw, kw)
                        if normalized == CONFIRMATION_TARGET_KEYWORD:
                            set_confirmation_found(normalized)
                            break

                
                # 如果正在搜索特定产品，则过滤（仅影响ID匹配）
                if self.current_search_target:
                    keywords = [k for k in keywords if k == self.current_search_target]
                    
                for keyword in keywords:
                    all_matches.append(DetectionResult(
                        keyword=keyword,
                        text=text,
                        vertices=vertices,
                        confidence=conf,
                        timestamp=time.time()
                    ))
                        
            if all_matches:
                return max(all_matches, key=lambda x: x.confidence)
                
            return None
            
        except Exception as e:
            logger.error(f"帧处理错误: {e}")
            return None

    def draw_detection(self, frame: np.ndarray, detection: DetectionResult) -> np.ndarray:
        """可视化检测结果"""
        result_img = frame.copy()
        
        if detection.vertices:
            # 绘制边框
            pts = np.array(detection.vertices, np.int32).reshape((-1, 1, 2))
            cv2.polylines(result_img, [pts], True, (0, 255, 0), 3)
            
            # 准备标签
            x, y = detection.vertices[0]
            label = f"{detection.keyword} ({detection.confidence:.2f})"
            
            # 如果有像素坐标信息，添加到标签中
            if detection.pixel_x is not None and detection.pixel_y is not None:
                label += f" @({detection.pixel_x}, {detection.pixel_y})"
            
            (text_width, text_height), _ = cv2.getTextSize(label, cv2.FONT_HERSHEY_SIMPLEX, 0.8, 2)
            
            # 背景矩形
            cv2.rectangle(result_img,
                         (int(x), int(y) - text_height - 10),
                         (int(x) + text_width, int(y)),
                         (0, 255, 0),
                         -1)
            
            # 文本
            cv2.putText(result_img,
                       label,
                       (int(x), int(y) - 5),
                       cv2.FONT_HERSHEY_SIMPLEX,
                       0.8,
                       (0, 0, 0),
                       2)
                       
        return result_img

    async def wait_for_start_command(self):
        """等待"시작"命令"""
        # 确保音频流开启
        if not self.voice.stream:
            success = await self.voice.start_audio_stream_with_retry()
            if not success:
                await self.handle_transition('stop_search')  # 回到安全状态
                return
            
        try:
            # 等待语音活动检测
            await asyncio.wait_for(self.voice.active_event.wait(), timeout=1.0)
            
            # 检测到语音后等待
            await asyncio.sleep(1.5)
            
            # 停止录音并处理
            voice_command = await self.voice.stop_recording_and_process()
            
            if voice_command and voice_command.command_type == 'start':
                logger.info("检测到'시작'命令")
                await self.handle_transition('voice_start')
                    
        except asyncio.TimeoutError:
            # 超时是正常的
            pass
        finally:
            self.voice.active_event.clear()

    async def wait_for_search_command(self):
        """等待搜索命令"""
        if not self.has_announced_prompt:
            # 发送语音提示消息（替代TTS）
            if connected_clients:
                prompt_message = {
                    'type': 'voice_prompt',
                    'message': '찾으시는 물건을 말씀해주세요',
                    'state': 'waiting_for_voice',
                    'timestamp': time.time()
                }
                _websockets_broadcast_fire_and_forget(connected_clients, json.dumps(prompt_message, ensure_ascii=False))
            self.has_announced_prompt = True
            await asyncio.sleep(1)
        
        # 确保音频流开启
        if not self.voice.stream:
            success = await self.voice.start_audio_stream_with_retry()
            if not success:
                await self.handle_transition('voice_stop')  # 转到暂停状态
                return
            
        try:
            # 等待语音活动检测
            await asyncio.wait_for(self.voice.active_event.wait(), timeout=10.0)
            
            # 检测到语音后等待
            await asyncio.sleep(2.0)
            
            # 停止录音并处理
            voice_command = await self.voice.stop_recording_and_process()
            
            if voice_command:
                if voice_command.command_type == 'search':
                    await self.set_search_target(voice_command.keyword)
                elif voice_command.command_type == 'stop':
                    # 发送暂停消息（替代TTS）
                    if connected_clients:
                        pause_message = {
                            'type': 'search_paused',
                            'message': '검색을 일시 중지합니다',
                            'timestamp': time.time()
                        }
                        # 使用已定义的 fire-and-forget wrapper，避免类型/静态检查报错
                        _websockets_broadcast_fire_and_forget(connected_clients, json.dumps(pause_message, ensure_ascii=False))
                    await self.handle_transition('voice_stop')
                    
        except asyncio.TimeoutError:
            logger.info("语音命令超时")
            # 发送超时消息（替代TTS）
            if connected_clients:
                timeout_message = {
                    'type': 'voice_timeout',
                    'message': '음성 명령을 받지 못했습니다. 다시 말씀해주세요',
                    'timestamp': time.time()
                }
                await broadcast(connected_clients, json.dumps(timeout_message, ensure_ascii=False))
        finally:
            self.voice.active_event.clear()


    async def ocr_worker(self):
        logger.info("OCR工人已启动...")
        DISCARD_DELTA = CONFIG.get('FRAME_MATCH_MAX_DELTA', 0.)  # 与“当前最新入队帧”的时间落后超过此值就丢弃结果
        try:
            while self.is_running:
                item = await self.ocr_task_queue.get()
                frame   = item['frame']
                recv_ts = item['recv_ts']
                meta_id = item['meta_id']
                meta    = item['meta']

                # 标记：开始一个 OCR 处理（用于 should_process_frame 上限控制）
                self.pending_ocr_count += 1
                try:
                    detection = await asyncio.to_thread(self.process_frame, frame)
                finally:
                    self.pending_ocr_count -= 1
                    self.ocr_task_queue.task_done()

                # 若本结果已落后于最新入队帧太久 → 丢弃
                if (self.latest_enqueued_ts - recv_ts) > DISCARD_DELTA:
                    logger.info(f"丢弃过期 OCR 结果：lag={self.latest_enqueued_ts - recv_ts:.3f}s > {DISCARD_DELTA}s")
                    continue

                if not detection:
                    continue

                # 成功检测后的处理（复用你的逻辑）
                self.last_detection_time = time.time()
                self.processed_frames_count += 1
                logger.info(f"✅ [工人] 检测到: {detection.keyword} (置信度 {detection.confidence:.2f})")

                if self.current_search_target and detection.keyword == self.current_search_target:
                    is_first_time = not self.has_announced_found
                    if is_first_time:
                        self.has_announced_found = True
                        self.fast_detection_mode = False
                        if not self.one_shot_found_text:
                            self.one_shot_found_text = "찾았습니다."

                    annotated_frame = self.draw_detection(frame, detection)
                    timestamp = int(time.time() * 1000)
                    cv2.imwrite(f"detection_{timestamp}.jpg", annotated_frame)

                    try:
                        self.detection_results.put_nowait({
                            'detection': asdict(detection),
                            'frame': annotated_frame,
                            'original_frame': frame
                        })
                    except queue.Full:
                        self.detection_results.get_nowait()
                        self.detection_results.put_nowait({
                            'detection': asdict(detection),
                            'frame': annotated_frame,
                            'original_frame': frame
                        })

                    # —— 新增：仅在门打开后才允许发送给 HoloLens ——
                    now = time.time()
                    if self.send_gate.on_hit(now):
                        if connected_clients:
                            _websockets_broadcast_fire_and_forget(connected_clients, json.dumps({
                                'type': 'detection',
                                'id': meta_id,
                                'keyword': detection.keyword,
                                'text': detection.text,
                                'confidence': detection.confidence,
                                'vertices': detection.vertices,
                                'timestamp': detection.timestamp,
                                'found': True,
                                'first_time': is_first_time,
                                'message': f'{detection.keyword}를 찾았습니다!',
                                'object_ocr_mode': self.object_ocr_mode
                            }, ensure_ascii=False))
                    else:
                        # 仍处于静默（HOLDING）期：不发送
                        logger.debug("SendGate holding... 首命中静默期内不发送给 HoloLens")

        except asyncio.CancelledError:
            logger.info("OCR工人已取消")



    async def process_stream(self):
        """主流处理循环 - 改进版"""
        self.is_running = True
        
        # 设置语音处理器的事件循环引用
        self.voice.set_event_loop(asyncio.get_event_loop())
        if self.ocr_worker_task is None or self.ocr_worker_task.done():
            self.ocr_worker_task = asyncio.create_task(self.ocr_worker())

        
        # 如果是拉取模式，先连接视频流
        if not self.use_pushed_frames:
            if not self.connect_stream():
                logger.error("视频流连接失败，无法启动")
                self.is_running = False
                return
        
        logger.info(f"系统启动 - 物体OCR模式支持（包含像素坐标）")
        
        # 广播系统就绪消息
        if connected_clients:
            ready_message = {
                'type': 'system_ready',
                'state': SystemState.STANDBY.value,
                'mode': 'push' if self.use_pushed_frames else 'pull',
                'timestamp': time.time(),
                'object_ocr_support': True,  # 新增：支持物体OCR
                'pixel_tracking': True  # 新增：支持像素坐标追踪
            }
            _websockets_broadcast_fire_and_forget(connected_clients, json.dumps(ready_message, ensure_ascii=False))
        
        stats_report_interval = 30
        last_stats_time = time.time()
        
        while self.is_running:
            try:
                # STANDBY, READY, PAUSE 状态处理保持不变
                if self.state != SystemState.ACTIVE:
                    await asyncio.sleep(0.1)
                    continue
                
                # ACTIVE 状态：如果启用了物体OCR模式，则跳过整帧处理
                if self.object_ocr_mode:
                    # 等待Unity发送物体图像进行OCR
                    await asyncio.sleep(0.1)
                    continue
                
                # 如果没有启用物体OCR模式，继续原有的整帧处理流程
                if not self.current_search_target:
                    if CONFIG.get('USE_PHONE_VOICE', True):
                        await asyncio.sleep(0.1)
                    else:
                        await self.wait_for_search_command()
                        await asyncio.sleep(0.1)
                    continue

                
                # 检查搜索超时
                if self.search_start_time and time.time() - self.search_start_time > self.search_timeout:
                    # 发送超时消息
                    if connected_clients:
                        timeout_message = {
                            'type': 'search_timeout',
                            'target': self.current_search_target,
                            'message': f'{self.current_search_target}를 찾을 수 없습니다',
                            'timestamp': time.time()
                        }
                        # 使用本模块内定义的异步 broadcast 函数并 await，避免静态分析器报错且确保正确发送
                        await broadcast(connected_clients, json.dumps(timeout_message, ensure_ascii=False))
                    self.clear_search_target()
                    continue
                
                # 获取帧（如果不是物体OCR模式）
                got = await self.get_frame()
                if not got:
                    continue
                frame, recv_ts = got

                # === 在所有 pending_meta 里找 与 recv_ts 最接近 的那个 ===
                meta_id, meta, pair_gap = None, {}, None
                if pending_meta:
                    best_key, best_meta, best_gap = None, None, float("inf")
                    for k, m in list(pending_meta.items()):
                        ts_val = m.get('ts') or m.get('timestamp')
                        if ts_val is None:
                            continue
                        sec= self._norm_ts_to_seconds(ts_val)

                        gap = abs(recv_ts - sec)
                        if gap < best_gap:
                            best_key, best_meta, best_gap = k, m, gap

                    if best_key is not None:
                        del pending_meta[best_key]
                        meta_id, meta, pair_gap = best_key, best_meta, best_gap

                # 配对时间差过大 → 丢弃本帧，避免错绑 ID
                if meta_id is not None and pair_gap is not None and pair_gap > CONFIG['FRAME_MATCH_MAX_DELTA']:
                    logger.warning(f"丢弃帧：帧/元信息配对时间差过大 gap={pair_gap:.3f}s > {CONFIG['FRAME_MATCH_MAX_DELTA']}s")
                    continue

                # 元信息过期 → 丢弃
                if meta and ('ts' in meta or 'timestamp' in meta):
                    sent_sec = self._norm_ts_to_seconds(meta.get('ts') or meta.get('timestamp'))
                    age = abs(recv_ts - sent_sec) # 
                    if age > CONFIG['STALE_FRAME_MAX_AGE']:
                        logger.warning(f"丢弃过期帧 meta_id={meta_id} | age={age:.2f}s > {CONFIG['STALE_FRAME_MAX_AGE']}s")
                        continue

                self.frame_count += 1

                # 智能节流：仍然沿用 should_process_frame()
                if not await self.should_process_frame():
                    self.skipped_frames_count += 1
                    continue

                # === 入 LIFO（只保留最新） ===
                task = {
                    'frame': frame,
                    'recv_ts': recv_ts,
                    'meta_id': meta_id,
                    'meta': meta,
                }
                try:
                    self.ocr_task_queue.put_nowait(task)
                except asyncio.QueueFull:
                    try:
                        _ = self.ocr_task_queue.get_nowait()  # 丢掉上一条等待中的任务
                        self.ocr_task_queue.task_done()       # ✅ 一定要配对，告诉队列这条“已处理”（选择性丢弃也算处理完）
                    except asyncio.QueueEmpty:
                        pass
                    self.ocr_task_queue.put_nowait(task)


                self.latest_enqueued_ts = recv_ts
                self.last_ocr_time = time.time()  # 继续沿用你的节流逻辑
                await asyncio.sleep(0.01)

                
            except Exception as e:
                logger.error(f"流处理错误: {e}", exc_info=True)
                await asyncio.sleep(1)

    def stop(self):
        """停止流处理"""
        self.is_running = False
        if self.cap is not None:
            self.cap.release()
            self.cap = None
        self.voice.stop()
        try:
            if self.ocr_worker_task:
                self.ocr_worker_task.cancel()
        except Exception:
            pass
        logger.info("流处理器已停止")

    def get_latest_detection(self) -> Optional[Dict]:
        """获取最新的检测结果"""
        try:
            latest = None
            while not self.detection_results.empty():
                latest = self.detection_results.get_nowait()
            return latest
        except queue.Empty:
            return None


async def main():
    """主函数"""
    import sys
    global global_processor
    
    # 设置流 URL
    stream_url = sys.argv[1] if len(sys.argv) > 1 else CONFIG['STREAM_URL']
    logger.info(f"HoloLens 物体OCR 流处理器启动")
    logger.info(f"流 URL: {stream_url}")
    logger.info(f"推送模式: {CONFIG['USE_PUSHED_FRAMES']}")
    
    # 如果使用推送模式，启动 FFmpeg
    if CONFIG['USE_PUSHED_FRAMES']:
        if ffmpeg_manager.start():
            logger.info("FFmpeg RTSP 服务器已启动")
        else:
            logger.error("FFmpeg 启动失败，但继续运行")
    
    # 初始化组件
    ocr_client = NaverOCR(CONFIG['NAVER_OCR_URL'], CONFIG['NAVER_SECRET_KEY'])
    voice_handler = VoiceInputHandler()
    processor = StreamProcessor(stream_url, ocr_client, voice_handler)
    
    # 设置全局处理器引用
    global_processor = processor
    
    # 启动 WebSocket 服务器
    ws_server = None
    frame_ws_server = None
    http_app = None
    http_runner = None
    http_site = None

    
    try:
        # 控制命令 WebSocket
        ws_server = await serve(ws_handler, '0.0.0.0', 5000)
        logger.info("WebSocket 控制服务器已启动: ws://0.0.0.0:5000")
        
        
        # 帧推送 WebSocket（仅在推送模式下启用）
        if CONFIG['USE_PUSHED_FRAMES']:
            frame_ws_server = await serve(frame_ws_handler, '0.0.0.0', 5001)
            logger.info("WebSocket 帧接收服务器已启动: ws://0.0.0.0:5001/frames")
            logger.info("Unity 应将 JPEG 帧推送到此地址")



        # ---- 轻量状态：对象缓存 + 粘性选择 ----
        OBJECT_CACHE = {"objects": [], "ts": 0.0, "size": (0, 0)}  # 最近一次有效 objects
        CACHE_TTL = float(os.environ.get('OBJECT_CACHE_TTL', '0.5'))  # 秒；objects 缺失时可用的有效期
        STICKY_MARGIN_PX = int(os.environ.get('STICKY_MARGIN_PX', '40'))  # 改判余量（px）


        # === HTTP 接口开始（端口 8008）===
        
        # ===============================================================
        # [新增] 确认模式端点（5秒实时检测）
        # ===============================================================
        async def handle_confirm(request):
            """
            处理物品确认请求
            POST /confirm
            JSON: {"keyword": "사브레"}
            
            工作流程：
            1. 收到请求后开始5秒倒计时
            2. 每0.1秒检查一次最近0.5秒内的OCR检测记录
            3. 如果检测到匹配的keyword → 立即返回is_match=True
            4. 5秒后仍未检测到 → 返回is_match=False
            """
            try:
                global CONFIRMATION_MODE_ACTIVE, CONFIRMATION_TARGET_KEYWORD, CONFIRMATION_FOUND_KEYWORD

                
                data = await request.json()
                keyword = data.get('keyword', '').strip()
                
                if not keyword:
                    return web.json_response({'is_match': False, 'error': 'No keyword'}, status=400)
                
                normalized = KEYWORD_MAPPING.get(keyword, keyword)
                logger.info(f"🔍 确认请求: {normalized}")
                
                with CONFIRMATION_LOCK:
                    CONFIRMATION_MODE_ACTIVE = True
                    CONFIRMATION_TARGET_KEYWORD = normalized
                    CONFIRMATION_FOUND_KEYWORD = None
                
                logger.info(f"🚩 确认模式已激活，目标: {normalized}")
                
                try:
                    start_time = time.time()
                    while time.time() - start_time < 5.0:
                        with CONFIRMATION_LOCK:
                            if CONFIRMATION_FOUND_KEYWORD == normalized:
                                elapsed = time.time() - start_time
                                logger.info(f"✅ 确认成功: {normalized} ({elapsed:.2f}秒)")
                                return web.json_response({'is_match': True, 'keyword': normalized})
                        await asyncio.sleep(0.1)
                    
                    logger.info(f"❌ 确认超时: {normalized}")
                    return web.json_response({'is_match': False, 'keyword': normalized})
                finally:
                    with CONFIRMATION_LOCK:
                        CONFIRMATION_MODE_ACTIVE = False
                        CONFIRMATION_TARGET_KEYWORD = None
                        CONFIRMATION_FOUND_KEYWORD = None
                    logger.info("🚫 确认模式已关闭")
                
            except Exception as e:
                CONFIRMATION_MODE_ACTIVE = False  # 异常情况也要关闭
                logger.error(f"❌ 确认请求处理错误: {e}")
                return web.json_response({
                    'is_match': False,
                    'error': str(e)
                }, status=500)
        
        async def handle_voice(request):
            """
            接收手机语音转文字后的整句文本：
            POST /voice
            JSON: {"text": "신라면 찾아줘"}
            """
            try:
                data = await request.json()
            except Exception:
                return web.json_response({"status": "error", "message": "请求体不是有效的 JSON"}, status=400)

            text_value = data.get("text")
            if isinstance(text_value, dict):
                # 如果text的值是一个词典，尝试从中提取第一个值
                text = str(next(iter(text_value.values()), ""))
            else:
                # 否则，按原样处理
                text = str(text_value or "")

            text = text.strip()

            # 解析整句
            if global_processor is None:
                return web.json_response({"status": "error", "message": "系统未准备好"}, status=503)

            cmd = global_processor.voice._parse_command(text)
            if not cmd:
                return web.json_response({"status": "no_command", "text": text})

            # 根据解析结果执行状态切换与动作
             # === search：允许“直接找X”，可选严格模式 ===
            if cmd.command_type == "search":
                # （可选）严格要求：必须先 start 才能 search
                if CONFIG.get('STRICT_START_REQUIRED', False):
                    if global_processor.state != SystemState.ACTIVE:
                        return web.json_response({
                            "status": "need_start",
                            "message": "먼저 '시작'이라고 말해주세요."
                    
                        })
                    
                

                # 默认：不是 ACTIVE 也会自动进入工作状态
                if global_processor.state != SystemState.ACTIVE:
                    if global_processor.state in (SystemState.STANDBY, SystemState.READY):
                        await global_processor.handle_transition('start_search')
                    elif global_processor.state == SystemState.PAUSE:
                        await global_processor.handle_transition('voice_start')

                    # 【关键】在开始新的搜索前，重置粘性选择的状态
                    request.app['SELECTION']['id'] = None

                # 设置搜索目标（会清空一次性“找到”标志）
                await global_processor.set_search_target(cmd.keyword)

                return web.json_response({
                    "status": "ok",
                    "parsed": {"type": "search", "keyword": cmd.keyword},
                    "text": text
                })


            elif cmd.command_type == "start":
                if global_processor.state != SystemState.ACTIVE:
                    if global_processor.state in (SystemState.STANDBY, SystemState.READY):
                        await global_processor.handle_transition('start_search')
                    elif global_processor.state == SystemState.PAUSE:
                        await global_processor.handle_transition('voice_start')
                return web.json_response({
                    "status": "ok",
                    "parsed": {"type": "start"},
                    "text": text
                })

            elif cmd.command_type == "stop":
                if global_processor.state == SystemState.ACTIVE:
                    await global_processor.handle_transition('voice_stop')
                else:
                    await global_processor.handle_transition('stop_search')
                return web.json_response({
                    "status": "ok",
                    "parsed": {"type": "stop"},
                    "text": text
                })

            return web.json_response({"status": "no_command", "text": text})


            
            # Helper functions
           # === 新增：物体OCR接口（改进版，支持像素坐标）===
        ocr_semaphore = asyncio.Semaphore(3) # 最多同时处理3个OCR请求

        async def handle_status(request):
            if global_processor is None:
                return web.Response(text="not_ready", content_type="text/plain")

            # ✅ 持久 found：不会被 panel 抢走
            if global_processor.has_announced_found:
                return web.Response(text="found", content_type="text/plain")

            state = global_processor.state
            if state == SystemState.STANDBY:
                return web.Response(text="idle", content_type="text/plain")
            if state == SystemState.ACTIVE and global_processor.current_search_target:
                return web.Response(text="ongoing", content_type="text/plain")
            return web.Response(text="waiting", content_type="text/plain")

        async def handle_ocr(request):
            """
            接收Unity发送的整帧图像和多个物体中心坐标进行OCR
            POST /ocr
            JSON: {
                "frame_id": 1,
                "image": "<base64>",
                "objects": [
                    {"object_id": "Detectable_Sphere1", "x": 123.4, "y": 567.8},
                    ...
                ]
            }
            返回: 命中则 JSON；否则 "not_found"
            """
            global latest_frame_jpeg, latest_annotated_jpeg, last_image_base64
            # ---- helper functions（只在本函数内部用）----
            def parse_objects(obj_list):
                parsed = []
                for obj in obj_list:
                    oid = str(obj.get("object_id", ""))
                    cx = obj.get("x"); cy = obj.get("y")
                    w  = obj.get("w", 0)
                    h  = obj.get("h", 0)
                    if not oid or cx is None or cy is None:
                        continue
                    try:
                        cx = float(cx); cy = float(cy)
                        w  = float(w) if w is not None else 0.0
                        h  = float(h) if h is not None else 0.0
                    except (ValueError, TypeError):
                        continue
                    parsed.append({"id": oid, "center": (cx, cy), "size": (w, h)})
                    print(f"解析物体: id={oid}, center=({cx}, {cy}), size=({w}, {h})")
                return parsed

            def euclidean_distance(p1, p2):
                return ((p1[0]-p2[0])**2 + (p1[1]-p2[1])**2) ** 0.5
            
            def calculate_match_score(ocr_center, obj_center, obj, config):
                """
                简化的匹配评分函数,只使用欧氏距离
                
                Args:
                    ocr_center: (x, y) OCR文字中心
                    obj_center: (x, y) 物体中心
                    obj: 物体字典(包含size等)
                    config: 配置字典
                
                Returns:
                    float: 匹配分数,越小越好
                """
                # 基础欧氏距离
                distance = math.hypot(ocr_center[0] - obj_center[0], 
                                     ocr_center[1] - obj_center[1])
                
                logger.debug(f"      评分详情: dist={distance:.1f}")
                
                return distance
            
            def get_valid_candidates(ocr_center, objects, max_radius):
                """
                预筛选:只保留合理范围内的候选物体
                
                Args:
                    ocr_center: (x, y) OCR中心
                    objects: 物体列表
                    max_radius: 最大搜索半径(像素)
                
                Returns:
                    list: 有效候选列表
                """
                candidates = []
                for obj in objects:
                    distance = math.hypot(ocr_center[0] - obj["center"][0],
                                         ocr_center[1] - obj["center"][1])
                    if distance <= max_radius:
                        candidates.append(obj)
                    else:
                        logger.debug(f"    -> 排除远距离候选 {obj['id']}: {distance:.1f}px > {max_radius}px")
                
                return candidates

            def aabb_from_vertices(vertices):
                if not vertices or len(vertices) != 4:
                    return None
                xs = [v[0] for v in vertices]
                ys = [v[1] for v in vertices]
                return ((min(xs)+max(xs))/2, (min(ys)+max(ys))/2)

            def match_keywords_for_text(text, matcher):
                return matcher.find_matches(text) if text else []

            def pick_best_text_hit(hits, objects):
                if not hits:
                    return None
                for hit in hits:
                    hit['min_object_distance'] = min(
                        (euclidean_distance(hit['center'], obj['center']) for obj in objects),
                        default=float('inf')
                    )
                return sorted(hits, key=lambda x: (-x['confidence'], x['min_object_distance']))[0]

            def nearest_object_id(point, objects):
                if not objects: return None
                px, py = point
                return min(objects, key=lambda o: (o["center"][0]-px)**2 + (o["center"][1]-py)**2)["id"]

            # ---- 主体逻辑（带并发限制）----
            async with ocr_semaphore:
                # 1) 解析请求
                try:
                    data = await request.json()
                    frame_timestamp = data.get("timestamp")
                    if frame_timestamp:
                        current_time = time.time()
                        logger.info(f"  -> [时间戳调试] PC当前时间: {current_time}, HoloLens发来的时间: {frame_timestamp}")
                        if global_processor:
                            age = abs(current_time - global_processor._norm_ts_to_seconds(frame_timestamp))
                            if age > 1.0:
                                logger.debug(f"[OCR] ts skew {age:.2f}s → 忽略时间，继续处理")
                                frame_timestamp = int(current_time * 1000)
                                data["timestamp"] = frame_timestamp
                except Exception as e:
                    logger.error(f"OCR请求JSON解析失败: {e}")
                    return web.Response(text="not_found", content_type="text/plain")

                frame_id = data.get("frame_id")
                if frame_id:
                    logger.info(f"处理帧 ID: {frame_id}")

                image_base64 = data.get("image")
                objects_raw = data.get("objects", [])

                if not image_base64:
                    logger.error("缺少image字段")
                    # 没图也返回 not_found
                    return web.Response(text="not_found", content_type="text/plain")




                # 2) 先把原始JPEG写进预览缓存（关键！即使后面不命中，也能在 /preview 看到图）
                try:
                    raw_bytes = base64.b64decode(image_base64)
                except Exception as e:
                    logger.error(f"Base64解码失败: {e}")
                    return web.Response(text="not_found", content_type="text/plain")

                # 更新最新原始帧缓存
                latest_frame_jpeg = raw_bytes

                # 去重判断放在“更新预览缓存之后”
                if image_base64 and image_base64 == last_image_base64:
                    logger.info("检测到重复帧，跳过处理。")
                    # 兜底：若还没有标注帧，就用原图占位，避免 /last_annotated.jpg 404
                    if latest_annotated_jpeg is None:
                        latest_annotated_jpeg = latest_frame_jpeg
                    return web.Response(text="not_found", content_type="text/plain")
                last_image_base64 = image_base64

                # 3) OpenCV 解码 + 轻量增强（做 OCR 用）
                np_arr = np.frombuffer(raw_bytes, np.uint8)
                img = cv2.imdecode(np_arr, cv2.IMREAD_COLOR)
                if img is None:
                    logger.error("OpenCV 解码失败，无法做增强")
                    # 标注帧也用原图兜底，避免404
                    latest_annotated_jpeg = latest_frame_jpeg
                    return web.Response(text="not_found", content_type="text/plain")

                # 局部对比度增强 + 保边降噪 + USM
                try:
                    lab = cv2.cvtColor(img, cv2.COLOR_BGR2LAB)
                    l, a, b = cv2.split(lab)
                    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
                    l = clahe.apply(l)
                    lab = cv2.merge([l, a, b])
                    img = cv2.cvtColor(lab, cv2.COLOR_LAB2BGR)
                    img = cv2.bilateralFilter(img, d=5, sigmaColor=50, sigmaSpace=50)
                    blur = cv2.GaussianBlur(img, (0, 0), 1.0)
                    img = cv2.addWeighted(img, 1.5, blur, -0.5, 0)
                    ok, buf = cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, 95])
                    ocr_image_bytes = buf.tobytes() if ok else raw_bytes
                except Exception as e:
                    logger.warning(f"图像增强失败，退回原图: {e}")
                    ocr_image_bytes = raw_bytes

                h, w = img.shape[:2]
                logger.info(f"/ocr 收到图像尺寸: {w}x{h}")                # 解码
                # 解析 objects（可能为空）
                objects = parse_objects(objects_raw) if (objects_raw is not None) else []
                now_sec = time.time()

                # 先尝试用请求里的 objects；没有的话再用缓存
                if not objects:
                    if request.app['OBJECT_CACHE']["objects"] and (now_sec - request.app['OBJECT_CACHE']["ts"]) <= request.app['CACHE_TTL']:
                        objects = request.app['OBJECT_CACHE']["objects"]
                        logger.info(f"objects 缺失 → 使用缓存 {len(objects)} 个")
                    else:
                        logger.info("objects 缺失且缓存过期；继续流程但可能返回 not_found")
                else:
                    # 正常收到 objects，更新缓存
                    request.app['OBJECT_CACHE']["objects"] = objects
                    request.app['OBJECT_CACHE']["ts"] = now_sec
                    request.app['OBJECT_CACHE']["size"] = (w, h)
        # --- (신규) 이미지 중심 기준 좌표 비율 축소 로직 ---
            SCALE_X = 1.2    # 가로 확대 (1.2 = 양옆으로 20% 넓게 퍼짐)
            SCALE_Y = 1.1    # 세로 확대 (1.2 = 위아래로 20% 길어짐)
            OFFSET_Y = 00.0  # 아래로 이동 (픽셀 단위, 양수면 전체적으로 내려감)

            if objects and w > 0 and h > 0:
                center_x = w / 2.0
                center_y = h / 2.0
                
                scaled_objects = []
                for obj in objects:
                    # 원본 객체 좌표
                    obj_x, obj_y = obj['center']
                    
                    # 1. 이미지 중심점에서 객체까지의 벡터 계산
                    vec_x = obj_x - center_x
                    vec_y = obj_y - center_y
                    
                    # 2. X축: 양방향 확대 (비율만 적용)
                    scaled_vec_x = vec_x * SCALE_X
                    
                    # 3. Y축: 확대 후 아래로 이동 (비율 적용 + 좌표 내리기)
                    # 비율을 키우면 위쪽 물체는 더 위로 올라가므로, OFFSET_Y를 더해 전체를 끌어내립니다.
                    scaled_vec_y = (vec_y * SCALE_Y) + OFFSET_Y
                    
                    # 4. 최종 좌표 계산
                    new_x = center_x + scaled_vec_x
                    new_y = center_y + scaled_vec_y
                    
                    # 5. 리스트 업데이트
                    new_obj = obj.copy()
                    new_obj['center'] = (new_x, new_y)
                    scaled_objects.append(new_obj)
                    
                objects = scaled_objects # 교체
                logger.info(f" -> 좌표 조정 적용: X배율={SCALE_X}, Y배율={SCALE_Y}, Y이동={OFFSET_Y}")
                # --- (신규) 로직 종료 ---
                logger.info(f"  -> 本次用于匹配的 objects 数量: {len(objects)}")


                # —— 暂停短路：跳过 OCR，用锁定的对象直接回传 —— 
                if global_processor and global_processor.recognition_paused:
                    locked_id = global_processor.lock_object_id or request.app['SELECTION'].get("id")
                    if not locked_id:
                        return web.Response(text="paused", content_type="text/plain")

                    target_obj = next((o for o in objects if o["id"] == locked_id), None)
                    if target_obj:
                        tx, ty = int(target_obj["center"][0]), int(target_obj["center"][1])
                        cv2.circle(img, (tx, ty), 10, (0, 255, 255), -1)
                        cv2.putText(img, f"LOCK {locked_id}", (tx+8, ty-8),
                                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0,0,0), 3)
                        cv2.putText(img, f"LOCK {locked_id}", (tx+8, ty-8),
                                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0,255,255), 2)

                    ok, jpeg = cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, 85])
                    if ok:
                        latest_annotated_jpeg = jpeg.tobytes()
                    else:
                        latest_annotated_jpeg = latest_frame_jpeg

                    resp = {
                        "status": "locked",
                        "object_id": locked_id,
                        "frame_id": frame_id,
                        "timestamp": frame_timestamp,
                        "message": "recognition paused - locked to current object",
                    }
                    if global_processor.last_ocr_hit:
                        hit = global_processor.last_ocr_hit
                        resp["ocr"] = {
                            "keyword": hit["keyword"],
                            "text": hit["text"],
                            "center": {"x": float(hit["center"][0]), "y": float(hit["center"][1])},
                            "vertices": hit.get("vertices", []),
                            "confidence": float(hit["confidence"]),
                        }
                    ua = request.headers.get("User-Agent", "")
                    if not any(tag in ua for tag in ("UnityPlayer", "UnityWebRequest")):
                        resp["annotated_image"] = base64.b64encode(latest_annotated_jpeg).decode("ascii")
                    return web.json_response(resp)




                # 4) 调用 OCR
                if global_processor is None or global_processor.ocr is None:
                    logger.error("OCR服务未就绪")
                    latest_annotated_jpeg = latest_frame_jpeg
                    return web.Response(text="not_found", content_type="text/plain")

                loop = asyncio.get_event_loop()
                try:
                    ocr_results = await loop.run_in_executor(
                        None, global_processor.ocr.recognize_text, ocr_image_bytes
                    )
                    logger.info(f"  -> 识别出的内容: {[r.get('text') for r in ocr_results]}")
                except Exception as e:
                    logger.error(f"OCR执行失败: {e}")
                    latest_annotated_jpeg = latest_frame_jpeg
                    return web.Response(text="not_found", content_type="text/plain")

                # 5) 关键词匹配
                hits = []
                for result in ocr_results:
                    text = result.get('text', '')
                    confidence = result.get('confidence', 0.0)
                    vertices = result.get('vertices', [])
                    if confidence < CONFIG.get('OCR_MIN_CONFIDENCE', 0.7):
                        continue

                    if global_processor and global_processor.matcher:
                        keywords = match_keywords_for_text(text, global_processor.matcher)
                        if keywords:
                            center = aabb_from_vertices(vertices)
                            if center:
                                cx, cy = center
                                cy += CONFIG['OCR_CENTER_Y_OFFSET']
                                cy = max(0, min(h - 1, cy))
                                center = (cx, cy)
                                for kw in keywords:
                                    hits.append({
                                        'keyword': kw,
                                        'text': text,
                                        'confidence': confidence,
                                        'center': center,
                                        'vertices': vertices
                                    })
                                    logger.info(f"匹配到关键词: {kw} (文本: {text}, 置信度: {confidence})")
                                    if center:
                                        logger.info(f"  -> OCR识别出的文本坐标: '{kw}' @ {center}")
                                    
                                    # ======= [新增] 确认模式快速通道 =======
                                    # 在确认模式下,只要检测到目标关键词就立即返回found,跳过所有ID匹配
                                    global CONFIRMATION_MODE_ACTIVE, CONFIRMATION_TARGET_KEYWORD, CONFIRMATION_FOUND_KEYWORD
                                    with CONFIRMATION_LOCK:
                                        if CONFIRMATION_MODE_ACTIVE and CONFIRMATION_TARGET_KEYWORD == kw:
                                            logger.info(f"✅ 确认模式：检测到目标关键词 '{kw}' - 直接返回found")
                                            # 直接设置变量，避免调用 set_confirmation_found 导致的死锁(重入锁问题)
                                            CONFIRMATION_FOUND_KEYWORD = kw
                                            logger.info(f"✅ 确认模式：检测到 '{kw}'")
                                            # 直接返回found响应，不进行任何ID匹配
                                            return web.Response(text="found", content_type="text/plain")
                                    # ======= [确认模式快速通道结束] =======

                # 如设置目标，则过滤到目标
                target = getattr(global_processor, "current_search_target", None)
                if target:
                    before = len(hits)
                    hits = [h for h in hits if h["keyword"] == target]
                    logger.info("根据搜索目标过滤: 目标=%s, 命中 %d -> %d", target, before, len(hits))

                if not hits:
                    logger.info("未找到匹配的关键词")
                    # 兜底：标注帧也用原图，避免 404
                    latest_annotated_jpeg = latest_frame_jpeg
                    return web.Response(text="not_found", content_type="text/plain")

                # 6) 选择最佳文本命中，映射最近物体ID，并绘制标注
                best_hit = pick_best_text_hit(hits, objects)
                # ---- 参考图优先：将OCR中心点映射到参考图坐标，按参考物体选ID ----
                ref = request.app.get('REFERENCE')
                nearest_id = None  # 先声明，便于后面逻辑复用
                ref_chosen_center_curr = None  # 仅用于画图兜底

                if ref and ref.get('objects'):
                    try:
                        # 此处的 img 已经在上文从latest_frame_jpeg解码得到
                        H = _homography_by_orb(img, ref['gray'], ref['kp'], ref['des'])
                        if H is not None and best_hit is not None:
                            # curr→ref 投点
                            pt = np.array([[best_hit["center"]]], dtype=np.float32)  # shape (1,1,2)
                            pt_ref = cv2.perspectiveTransform(pt, H)[0][0]           # (x_ref, y_ref)
                            # 在参考图物体上选最近
                            if ref['objects']:
                                px, py = float(pt_ref[0]), float(pt_ref[1])
                                nearest_id = min(
                                    ref['objects'],
                                    key=lambda o: (o["center"][0]-px)**2 + (o["center"][1]-py)**2
                                )["id"]
                                # 记录这个参考物体的“参考中心点”，后面若当前帧没此物体，也能画反投影点
                                ref_center = next((o["center"] for o in ref['objects'] if o["id"]==nearest_id), None)
                                if ref_center is not None:
                                    # 反投影：ref→curr，便于在当前局部视角也能落点画圈
                                    H_inv = np.linalg.inv(np.vstack([H, [0,0,1]]))[:3,:] if H is not None else None
                                    if H_inv is not None:
                                        rp = np.array([[[ref_center[0], ref_center[1]]]], dtype=np.float32)
                                        rp_curr = cv2.perspectiveTransform(rp, H_inv)[0][0]
                                        ref_chosen_center_curr = (int(rp_curr[0]), int(rp_curr[1]))
                    except Exception as e:
                        logger.warning(f"[REF] homography failed, fallback. err={e}")
                # ---- 参考图优先结束 ----

                if not best_hit:
                    latest_annotated_jpeg = latest_frame_jpeg
                    return web.Response(text="not_found", content_type="text/plain")

                if getattr(global_processor, "current_search_target", None) == best_hit["keyword"]:
                    cx, cy = best_hit["center"]
                    logger.info(f"  -> 目标 '{best_hit['keyword']}' 的OCR坐标: ({cx:.1f}, {cy:.1f})")
                    
                    # 输出所有候选物体的坐标,便于调试
                    logger.info(f"  -> 物体候选列表 (共{len(objects)}个):")
                    for i, obj in enumerate(objects[:8]):  # 只显示前8个
                        obj_x, obj_y = obj['center']
                        logger.info(f"     #{i+1} {obj['id']}: ({obj_x:.1f}, {obj_y:.1f})")

                def _euclid(a, b): return math.hypot(a[0]-b[0], a[1]-b[1])

                nearest_id = None
                if nearest_id is None and objects:
                    # 优先用参考图选出的 ID
                    nearest_id = nearest_object_id(best_hit["center"], objects)

                
                if objects:
                    # === 新的智能匹配逻辑 ===
                    ocr_center = best_hit["center"]
                    
                    # 步骤1: 候选预筛选
                    max_radius = CONFIG.get('MAX_CANDIDATE_RADIUS', 200)
                    candidates = get_valid_candidates(ocr_center, objects, max_radius)
                    
                    if not candidates:
                        logger.info(f"  -> 预筛选后无有效候选(搜索半径={max_radius}px)")
                        # 降级:使用所有物体重新尝试
                        candidates = objects
                        logger.info(f"  -> 降级为全局搜索,候选数={len(candidates)}")
                    else:
                        logger.info(f"  -> 预筛选保留 {len(candidates)}/{len(objects)} 个候选")
                    
                    # 步骤2: 使用改进的评分函数选择最佳候选
                    best_match_cand = None
                    min_score = float('inf')
                    
                    for obj in candidates:
                        score = calculate_match_score(ocr_center, obj["center"], obj, CONFIG)
                        
                        logger.debug(f"    -> 候选 {obj['id']}: 总分={score:.1f}, 坐标{obj['center']}")
                        
                        if score < min_score:
                            min_score = score
                            best_match_cand = obj
                    
                    cand = best_match_cand
                    # === 智能匹配逻辑结束 ===

                    if cand: # 确保找到了候选对象
                        nearest_id = cand["id"]
                        logger.info(f"  ✓ 最佳匹配: {nearest_id} (得分={min_score:.1f})")


                if nearest_id and best_hit and objects:
                        # 1. 找到这个 id 对应的物体
                        target_obj = next((o for o in objects if o.get("id") == nearest_id), None)
                        
                        if target_obj:
                            # 2. 提取坐标
                            ocr_pos = best_hit.get("center")
                            obj_pos = target_obj.get("center")
                            
                            if ocr_pos and obj_pos:
                                # 3. 计算纯粹的欧氏距离 (Euclidean distance)
                                # (确保 math 模块已导入, "import math")
                                dist = math.hypot(ocr_pos[0] - obj_pos[0], ocr_pos[1] - obj_pos[1])
                                
                                # 4. 从 CONFIG 获取阈值
                                max_dist = CONFIG.get('OCR_OBJECT_MAX_DISTANCE', 150) # 假设您已在CONFIG中添加
                                
                                # 5. 验证
                                if dist > max_dist:
                                    logger.info(f"  -> 匹配 {nearest_id} 被丢弃：纯距离过远 ({dist:.1f}px > {max_dist}px)")
                                    # 距离太远，认为匹配失败
                                    nearest_id = None
                                else:
                                    # 验证通过
                                    logger.info(f"  -> 匹配 {nearest_id} 验证通过：纯距离 {dist:.1f}px <= {max_dist}px")
                            else:
                                logger.warning(f"  -> 匹配 {nearest_id} 缺少坐标，无法验证距离，已丢弃")
                                nearest_id = None # 缺少数据，匹配失败
                        else:
                            logger.warning(f"  -> 逻辑错误： nearest_id {nearest_id} 在 objects 列表中未找到")
                            nearest_id = None # 逻辑错误，匹配失败            
                # 记录这次选择，形成“粘性”
                request.app['SELECTION']["id"] = nearest_id

                if global_processor:
                    global_processor.last_selected_object_id = nearest_id
                    global_processor.last_ocr_hit = best_hit  # 保存最近一次 OCR 命中

                if not nearest_id:
                    latest_annotated_jpeg = latest_frame_jpeg
                    return web.Response(text="not_found", content_type="text/plain")

                now = time.time()
                if global_processor.send_gate.on_hit(now):
                    global_processor.has_announced_found = True
                    if not global_processor.one_shot_found_text:
                        global_processor.one_shot_found_text = "찾았습니다."
                        


                result_id = f"{(frame_id or int(time.time()*1000))}-{nearest_id}-{uuid.uuid4().hex[:6]}"

                # 绘制标注（在 img 上）
                for obj in objects:
                    ox, oy = int(obj["center"][0]), int(obj["center"][1])
                    cv2.circle(img, (ox, oy), 6, (200, 200, 200), -1)

                target_obj = next((o for o in objects if o["id"] == nearest_id), None)
                if target_obj:
                    tx, ty = int(target_obj["center"][0]), int(target_obj["center"][1])
                    tw, th = target_obj.get("size", (0, 0))
                    cv2.circle(img, (tx, ty), 10, (0, 255, 255), -1)
                    obj_label = f"{nearest_id} ({tx},{ty})"
                    cv2.putText(img, obj_label, (tx+8, ty-8),
                                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0,0,0), 3)
                    cv2.putText(img, obj_label, (tx+8, ty-8),
                                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0,255,255), 2)
                    if tw > 0 and th > 0:
                        x1 = int(tx - tw/2); y1 = int(ty - th/2)
                        x2 = int(tx + tw/2); y2 = int(ty + th/2)
                        cv2.rectangle(img, (x1, y1), (x2, y2), (0, 255, 255), 2)

                verts = best_hit.get("vertices") or []
                if len(verts) == 4:
                    pts = np.array(verts, dtype=np.int32).reshape((-1,1,2))
                    cv2.polylines(img, [pts], True, (0, 255, 0), 3)
                cx, cy = int(best_hit["center"][0]), int(best_hit["center"][1])
                cv2.circle(img, (cx, cy), 10, (0, 255, 0), -1)
                ocr_label = f"OCR {best_hit['keyword']} ({cx},{cy})"
                cv2.putText(img, ocr_label, (cx+8, cy-8),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0,0,0), 3)
                cv2.putText(img, ocr_label, (cx+8, cy-8),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0,255,0), 2)

                header = f"result_id: {result_id}"
                cv2.rectangle(img, (10, 10), (10 + 8*len(header), 40), (0, 0, 0), -1)
                cv2.putText(img, header, (16, 32),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255,255,255), 2)

                ok, jpeg = cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, 85])
                if ok:
                    latest_annotated_jpeg = jpeg.tobytes()
                    # 可选：保存到磁盘
                    out_dir = "annotated"
                    os.makedirs(out_dir, exist_ok=True)
                    with open(os.path.join(out_dir, f"{result_id}.jpg"), "wb") as f:
                        f.write(latest_annotated_jpeg)
                else:
                    logger.error("标注图像编码失败，标注预览回退为原图")
                    latest_annotated_jpeg = latest_frame_jpeg




                # annotated_b64 = base64.b64encode(latest_annotated_jpeg).decode("ascii")

                response_data = {
                    "object_id": nearest_id,
                    "timestamp": frame_timestamp,
                    "frame_id": frame_id,
                    "result_id": result_id,
                    "image_size": {"width": w, "height": h},
                    "ocr": {
                        "keyword": best_hit["keyword"],
                        "text": best_hit["text"],
                        "center": {"x": best_hit["center"][0], "y": best_hit["center"][1]},
                        "vertices": best_hit["vertices"],
                        "confidence": best_hit["confidence"],
                    },
                 # "annotated_image": annotated_b64  # base64 的 JPEG（带标注）
                }
                ua = request.headers.get("User-Agent", "")
                if not any(tag in ua for tag in ("UnityPlayer", "UnityWebRequest")):
                    response_data["annotated_image"] = base64.b64encode(latest_annotated_jpeg).decode("ascii")
                now = time.time()
                if global_processor and not global_processor.send_gate.on_hit(now):
                    # ⛔ 静默期：对外一律“未命中”，避免 3 秒内生成 Cube
                    return web.Response(text="not_found", content_type="text/plain")

                return web.json_response(response_data)

                
        async def handle_last_frame(request):
            """返回最新一帧原始 JPEG"""
            if latest_frame_jpeg is None:
                return web.Response(text="no_frame", content_type="text/plain", status=404)
            return web.Response(
                body=latest_frame_jpeg,
                content_type="image/jpeg",
                headers={"Cache-Control": "no-store"}
            )

        async def handle_last_annotated(request):
            """返回最新一帧标注后的 JPEG"""
            if latest_annotated_jpeg is None:
                return web.Response(text="no_annotated", content_type="text/plain", status=404)
            return web.Response(
                body=latest_annotated_jpeg,
                content_type="image/jpeg",
                headers={"Cache-Control": "no-store"}
            )

        async def handle_panel(request):
            html = r"""<!doctype html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8" />
        <title>HoloLens 控制面板</title>
        <meta name="viewport" content="width=device-width,initial-scale=1" />
        <style>
        body{margin:0;background:#0f1115;color:#e6e6e6;font:14px/1.5 system-ui,Segoe UI,Roboto}
        .top{position:sticky;top:0;background:#151823;border-bottom:1px solid #23293a;padding:12px}
        .grid{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin:12px}
        .card{background:#141824;border:1px solid #23293a;border-radius:12px;padding:12px}
        button{background:#2b3245;border:1px solid #3a425b;border-radius:10px;padding:10px 14px;color:#e6e6e6;cursor:pointer}
        button:hover{filter:brightness(1.08)}
        input,select{background:#0f1320;color:#e6e6e6;border:1px solid #28314a;border-radius:8px;padding:8px}
        .row{display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin:8px 0}
        .imgs{display:flex;gap:10px;flex-wrap:wrap}
        .imgs img{max-width:48vw;height:auto;border:1px solid #23293a;border-radius:8px}
        .ok{color:#73d77e}.warn{color:#ffd166}.err{color:#ff6b6b}
        .mono{font-family:ui-monospace, SFMono-Regular, Menlo, Consolas, monospace}
        small{opacity:.8}
        </style>
        </head>
        <body>
        <div class="top">
            <b>HoloLens 控制面板</b>
            <small id="statusTxt" style="margin-left:10px"></small>
        </div>

        <div class="grid">
            <div class="card">
            <h3>控制</h3>
            <div class="row">
                <button id="btnStart">开始（start_search）</button>
                <button id="btnStop">停止（stop_search）</button>
            </div>
            <div class="row">
                <input id="lockId" placeholder="可选：指定要锁定的 object_id" style="min-width:260px">
                <button id="btnPause">暂停（并锁定）</button>
                <button id="btnResume">恢复</button>
                <button id="btnReset">复位</button>

            <div class="row">
                <select id="idList" style="min-width:260px">
                    <option value="">（从参考图读取 ID 列表）</option>
                </select>
                <button id="btnReloadIds">刷新ID列表</button>
            </div>

            </div>
            <div class="row">
                <button id="btnRefCap">拍照并设为参考</button>
                <button id="btnRefClr">清空参考</button>
            </div>
           
            <div>
                <div>参考图（Reference）</div>
                <img id="ref" src="/reference.jpg" />
            </div>


            <div class="row">
                <input id="keyword" placeholder="可选：设定关键词（如：진라면）" style="min-width:220px">
                <button id="btnSetKw">设定关键词</button>
                <select id="kwQuick">
                <option value="">常用快捷关键词</option>
                <option>신라면</option><option>진라면</option><option>삼양라면</option>
                <option>사브레</option><option>땅콩샌드</option><option>버터링</option>
                </select>
            </div>
            <div class="row mono" id="log" style="white-space:pre-wrap;max-height:200px;overflow:auto;background:#0d101a;padding:10px;border-radius:8px;border:1px solid #23293a"></div>
            </div>

            <div class="card">
            <h3>预览</h3>
            <div class="row">
                <button id="btn1s">1秒刷新</button>
                <button id="btn200ms">200ms刷新</button>
                <button id="btnManual">暂停刷新</button>
            </div>
            <div class="imgs">
                <div>
                <div>原始帧</div>
                <img id="raw" src="/last_frame.jpg" />
                </div>
                <div>
                <div>标注帧</div>
                <img id="ann" src="/last_annotated.jpg" />
                </div>
            </div>
            </div>
        </div>

        <script>
        const logEl = document.getElementById('log');
        function log(msg, cls="") {
        const t = new Date().toLocaleTimeString();
        logEl.innerHTML = `<span class="${cls}">[${t}] ${msg}</span>\n` + logEl.innerHTML;
        }

        async function postControl(action, lockId) {
        const body = lockId ? {action, lock_id: lockId} : {action};
        const r = await fetch('/control', {
            method: 'POST',
            headers: {'Content-Type': 'application/json'},
            body: JSON.stringify(body)
        });
        const txt = await r.text();
        try { return JSON.parse(txt); } catch { return {raw: txt}; }
        }

        async function getStatus() {
        try {
            const r = await fetch('/status');
            const t = await r.text();
            document.getElementById('statusTxt').textContent = `状态: ${t}`;
        } catch(e) {}
        }

        async function postJSON(url, body){
        const r = await fetch(url, {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body||{})});
        const txt = await r.text(); try{ return JSON.parse(txt) }catch{ return {raw:txt} }
        }

        document.getElementById('btnRefCap').onclick = async ()=>{
        const resp = await postJSON('/reference/capture', {});
        log('reference/capture -> ' + JSON.stringify(resp), resp.status==='ok'?'ok':'warn');
        // 刷新参考图
        document.getElementById('ref').src = '/reference.jpg?t=' + Date.now();
        };

        document.getElementById('btnRefClr').onclick = async ()=>{
        const resp = await postJSON('/reference/clear', {});
        log('reference/clear -> ' + JSON.stringify(resp), resp.status==='ok'?'ok':'warn');
        document.getElementById('ref').src = '/reference.jpg?t=' + Date.now(); // 可能 404，正常
        };


        // 预览刷新
        let interval = null;
        function setRefresh(ms){
        if (interval) clearInterval(interval);
        if (ms>0){
            interval = setInterval(()=>{
            const ts = Date.now();
            document.getElementById('raw').src = '/last_frame.jpg?t=' + ts;
            document.getElementById('ann').src = '/last_annotated.jpg?t=' + ts;
            getStatus();
            }, ms);
        }
        }
        setRefresh(0);

        // WebSocket 控制（开始/停止/设定关键词）
        let ws = null;
        function wsConnect(){
        try{
            ws = new WebSocket(`ws://${location.hostname}:5000/`);
            ws.onopen  = ()=> log('WS 连接成功', 'ok');
            ws.onclose = ()=> log('WS 连接关闭', 'warn');
            ws.onerror = (e)=> log('WS 错误: ' + e, 'err');
            ws.onmessage = (ev)=> {
            log('WS <= ' + ev.data);
            try{
                const j = JSON.parse(ev.data);
                if (j.type === 'state_changed') getStatus();
            }catch{}
            };
        }catch(e){
            log('WS 连接失败：' + e, 'err');
        }
        }
        wsConnect();

        function wsSend(cmd, payload={}){
        if (!ws || ws.readyState !== 1){
            log('WS 未连接，尝试重连...', 'warn');
            wsConnect();
            setTimeout(()=>{ if (ws && ws.readyState===1) ws.send(JSON.stringify({command: cmd, ...payload})) }, 500);
            return;
        }
        ws.send(JSON.stringify({command: cmd, ...payload}));
        log(`WS => ${cmd} ${JSON.stringify(payload)}`, 'ok');
        }

        // 绑定按钮
        document.getElementById('btnStart').onclick = ()=> wsSend('start_search');
        document.getElementById('btnStop').onclick  = ()=> wsSend('stop_search');

        document.getElementById('btnPause').onclick = async ()=>{
        const id = document.getElementById('lockId').value.trim() || undefined;
        const resp = await postControl('pause', id);
        log('pause -> ' + JSON.stringify(resp), 'ok');
        };
        document.getElementById('btnResume').onclick = async ()=>{
        const resp = await postControl('resume');
        log('resume -> ' + JSON.stringify(resp), 'ok');
        };
        document.getElementById('btnReset').onclick = async ()=>{
        const resp = await postControl('reset');
        log('reset -> ' + JSON.stringify(resp), 'ok');
        };

        document.getElementById('btnSetKw').onclick = ()=>{
        const kw = document.getElementById('keyword').value.trim();
        if (kw) wsSend('set_keywords', {keywords:[kw]});
        };
        document.getElementById('kwQuick').onchange = (e)=>{
        if (e.target.value) {
            document.getElementById('keyword').value = e.target.value;
            wsSend('set_keywords', {keywords:[e.target.value]});
        }
        };

        async function loadIdList() {
            try {
                const resp = await fetch('/reference/objects');
                const data = await resp.json();
                const sel = document.getElementById('idList');
                sel.innerHTML = '<option value="">（从参考图读取 ID 列表）</option>';
                (data.ids || []).forEach(id => {
                const opt = document.createElement('option');
                opt.value = id;
                opt.textContent = id;
                sel.appendChild(opt);
                });
            } catch (e) {
                console.error('加载参考图ID失败', e);
            }
            }

            // 下拉选择时把选中的 ID 写回 lockId 输入框，避免手打
            document.getElementById('idList').onchange = (e) => {
            if (e.target.value) document.getElementById('lockId').value = e.target.value;
            };

            // 刷新按钮：点击就重载一次
            document.getElementById('btnReloadIds').onclick = () => loadIdList();

            // 拍照设为参考后也刷新一下 ID 列表（你已有 btnRefCap 绑定）
            const _oldOnClickRefCap = document.getElementById('btnRefCap').onclick;
            document.getElementById('btnRefCap').onclick = async () => {
            await _oldOnClickRefCap();  // 先调用原有拍照逻辑
            setTimeout(loadIdList, 300); // 300ms 后刷新下拉
            };


        document.getElementById('btn1s').onclick = ()=> setRefresh(1000);
        document.getElementById('btn200ms').onclick = ()=> setRefresh(200);
        document.getElementById('btnManual').onclick = ()=> setRefresh(0);

        getStatus();
        </script>
        </body></html>"""
            return web.Response(text=html, content_type="text/html")
        


        # 就放在 handle_panel 定义的上/下方都可以
        async def handle_preview(request):
            # 兼容旧链接，直接跳转到 /panel
            raise web.HTTPFound('/panel')

        # ==== 参考图：捕获 / 清空 ====
        def _orb_index(gray):
            # Create an ORB detector in a way that is compatible with different OpenCV builds/type stubs.
            # Some builds expose cv2.ORB_create, others expose cv2.ORB.create, and in rare cases neither is
            # available (then fall back to SIFT if present). This avoids "ORB_create is not an attribute" errors.
            try:
                orb = None
                # Try cv2.ORB_create (most common)
                if hasattr(cv2, "ORB_create"):
                    orb = getattr(cv2, "ORB_create")(nfeatures=2000, scaleFactor=1.2, nlevels=8, edgeThreshold=15, patchSize=31)
                else:
                    # Try cv2.ORB.create (alternative)
                    OrbClass = getattr(cv2, "ORB", None)
                    if OrbClass is not None and hasattr(OrbClass, "create"):
                        orb = OrbClass.create(nfeatures=2000, scaleFactor=1.2, nlevels=8, edgeThreshold=15, patchSize=31)
                
                # Fallback to SIFT if ORB is unavailable
                if orb is None:
                    if hasattr(cv2, "SIFT_create"):
                        orb = getattr(cv2, "SIFT_create")()
                        logger.warning("cv2.ORB_create not found — using SIFT_create as fallback (matching behavior may differ).")
                    else:
                        raise RuntimeError("No ORB or SIFT feature detector available in this cv2 build")
                
                if orb is None:
                    return [], None
                    
                kp, des = orb.detectAndCompute(gray, None)
                return kp, des
            except Exception as e:
                logger.warning(f"_orb_index: failed to create feature detector or compute descriptors: {e}")
                return [], None

        def _homography_by_orb(curr_bgr, ref_gray, ref_kp, ref_des, min_inliers=20):
            curr_gray = cv2.cvtColor(curr_bgr, cv2.COLOR_BGR2GRAY)
            kp2, des2 = _orb_index(curr_gray)
            if des2 is None or ref_des is None or len(kp2) < 8 or len(ref_kp) < 8:
                return None
            bf = cv2.BFMatcher(cv2.NORM_HAMMING, crossCheck=False)
            matches = bf.knnMatch(ref_des, des2, k=2)
            good = [m for m,n in matches if m.distance < 0.75*n.distance]  # Lowe 比例
            if len(good) < min_inliers:
                return None
            src = np.asarray([ref_kp[m.queryIdx].pt for m in good], dtype=np.float32).reshape(-1,1,2)
            dst = np.asarray([kp2[m.trainIdx].pt for m in good], dtype=np.float32).reshape(-1,1,2)

            H, mask = cv2.findHomography(dst, src, cv2.RANSAC, 3.0)  # curr→ref
            if H is None: 
                return None
            if mask is not None and int(mask.sum()) < min_inliers:
                return None
            return H
        


        # === 新增：拍照建立参考图 ===
        async def handle_reference_capture(request):
            """用最新一帧缓存建立参考图索引（ORB + 当前 objects）"""
            global latest_frame_jpeg
            if latest_frame_jpeg is None:
                return web.json_response({"status": "error", "message": "no latest frame"}, status=409)

            nparr = np.frombuffer(latest_frame_jpeg, np.uint8)
            ref_bgr = cv2.imdecode(nparr, cv2.IMREAD_COLOR)
            if ref_bgr is None:
                return web.json_response({"status":"error","message":"decode failed"}, status=400)

            ref_gray = cv2.cvtColor(ref_bgr, cv2.COLOR_BGR2GRAY)
            kp, des = _orb_index(ref_gray)

            cache = request.app.get('OBJECT_CACHE') or {}
            objs = cache.get('objects', [])
            ref_objects = []
            for o in objs:
                if 'id' in o and 'center' in o:
                    ref_objects.append({"id": o['id'], "center": tuple(o['center'])})

            request.app['REFERENCE'] = {
                "bgr": ref_bgr, "gray": ref_gray, "kp": kp, "des": des,
                "objects": ref_objects, "size": (ref_bgr.shape[1], ref_bgr.shape[0]),
                "ts": time.time()
            }
            return web.json_response({"status":"ok","objects":len(ref_objects),"kp":len(kp)})

        

        

        async def handle_reference_image(request):
            """返回当前参考图 JPEG（若还没拍则 404）"""
            ref = request.app.get('REFERENCE')
            if not ref or 'bgr' not in ref:
                return web.Response(text="no_reference", status=404, content_type="text/plain")
            ok, jpeg = cv2.imencode(".jpg", ref['bgr'], [cv2.IMWRITE_JPEG_QUALITY, 90])
            if not ok:
                return web.Response(text="encode_fail", status=500, content_type="text/plain")
            return web.Response(body=jpeg.tobytes(), content_type="image/jpeg")
        



        async def handle_reference_objects(request):
            """返回参考图缓存里的对象 ID 列表，供网页端下拉选择"""
            ref = request.app.get('REFERENCE')
            ids = [o["id"] for o in ref.get("objects", [])] if ref else []
            return web.json_response({"ids": ids})


        async def handle_reference_clear(request):
            request.app['REFERENCE'] = None
            return web.json_response({"status":"ok"})



        # === 控制接口：暂停 / 恢复 / 复位 ===
        async def handle_control(request):
            if global_processor is None:
                return web.json_response({"status": "error", "message": "system not ready"}, status=503)
            try:
                data = await request.json()
            except Exception:
                return web.json_response({"status": "error", "message": "invalid json"}, status=400)

            action = (data.get("action") or "").lower()
            if action == "pause":
                lock_id = data.get("lock_id")  # 可选：指定要锁定的对象 ID
                locked = await global_processor.pause_lock(lock_id=lock_id)

                # === 新增：手动锁定后即刻触发一次“找到了”的单次播报 & 可选 WS 通知 ===
                try:
                    global_processor.has_announced_found = True
                    global_processor.one_shot_found_text = "찾았습니다."  # /status 会读一次后清空 → 手机端播报一次

                    # 给网页/HoloLens 的 WS（如果你那边也监听）一个“手动锁定”提示
                    if connected_clients:
                        _websockets_broadcast_fire_and_forget(
                            connected_clients,
                            json.dumps({
                                "type": "manual_lock",
                                "object_id": locked,
                                "timestamp": time.time(),
                                "message": "手动锁定：找到目标"
                            }, ensure_ascii=False)
                        )
                except Exception as _:
                    pass

                return web.json_response({"status": "ok", "action": "pause", "locked_id": locked})
            

            

            elif action == "resume":
                await global_processor.resume_recognition()
                return web.json_response({"status": "ok", "action": "resume"})

            elif action == "reset":
                # 清空 HTTP 侧缓存
                request.app['motion'].reset()
                request.app['OBJECT_CACHE'].update({"objects": [], "ts": 0.0, "size": (0, 0)})
                request.app['SELECTION']["id"] = None
                await global_processor.reset_system()
                return web.json_response({"status": "ok", "action": "reset", "state": global_processor.state.value})

            else:
                return web.json_response({"status": "error", "message": f"unknown action: {action}"}, status=400)




        # 启动 aiohttp HTTP 服务器 
        http_app = web.Application(client_max_size=100*1024*1024)
        http_app.add_routes([
            web.post('/voice', handle_voice),
            web.post('/ocr', handle_ocr),  # 新增OCR接口
            web.post('/confirm', handle_confirm),  # [新增] 确认模式接口
            web.get('/status', handle_status), # 新增状态查询接口
            web.get('/last_frame.jpg', handle_last_frame),
            web.get('/last_annotated.jpg', handle_last_annotated),
            web.get('/preview', handle_preview),
            web.post('/control', handle_control),
            web.get('/panel', handle_panel),
            web.post('/reference/capture', handle_reference_capture),
            web.post('/reference/clear', handle_reference_clear),
            web.get('/reference.jpg', handle_reference_image),
            web.get('/reference/objects', handle_reference_objects),

        ])
            # ---- 初始化并共享状态变量 ----

        http_app['OBJECT_CACHE'] = {"objects": [], "ts": 0.0, "size": (0, 0)}
        http_app['SELECTION'] = {"id": None}
        http_app['CACHE_TTL'] = float(os.environ.get('OBJECT_CACHE_TTL', '0.5'))
        http_app['STICKY_MARGIN_PX'] = int(os.environ.get('STICKY_MARGIN_PX', '40'))
        http_app['REFERENCE'] = None  # 可选参考对象 ID
        
        http_runner = web.AppRunner(http_app, access_log_class=FilteredAccessLogger)
        await http_runner.setup()
        http_site = web.TCPSite(http_runner, host='0.0.0.0', port=8008)
        await http_site.start()
        logger.info("HTTP 接口已启动:")
        logger.info("  - 语音文本: POST http://0.0.0.0:8008/voice")
        logger.info("  - 物体OCR: POST http://0.0.0.0:8008/ocr (支持像素坐标)")
        # === HTTP 接口结束 ===
        
        logger.info("系统处于 STANDBY 状态，等待 Unity 发送 'start_search' 命令或物体OCR请求")
        logger.info("像素坐标追踪功能已启用")
        
    except Exception as e:
        logger.error(f"服务器启动失败: {e}")
    
    try:
        # 运行主处理循环
        await processor.process_stream()
    except KeyboardInterrupt:
        logger.info("收到程序终止请求")
    except Exception as e:
        logger.error(f"意外错误: {e}", exc_info=True)
    finally:
        # 清理
        processor.stop()
        global_processor = None
        
        if ws_server:
            ws_server.close()
            await ws_server.wait_closed()
            logger.info("WebSocket 控制服务器已停止")
            
        if frame_ws_server:
            frame_ws_server.close()
            await frame_ws_server.wait_closed()
            logger.info("WebSocket 帧服务器已停止")

        if http_runner:
            try:
                await http_runner.cleanup()
                logger.info("HTTP 接口已停止")
            except Exception as e:
                logger.error(f"HTTP 接口关闭错误: {e}")
            
        # 停止 FFmpeg
        if CONFIG['USE_PUSHED_FRAMES']:
            ffmpeg_manager.stop()


if __name__ == "__main__":
    # 运行事件循环
    try:
        asyncio.run(main())
    except Exception as e:
        logger.error(f"程序运行失败: {e}", exc_info=True)