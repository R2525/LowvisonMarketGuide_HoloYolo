import 'dart:async';
import 'dart:io';
import 'dart:convert'; // JSON decode, LineSplitter

import 'package:flutter/material.dart';
import 'package:flutter_tts/flutter_tts.dart';
import 'package:record/record.dart';
import 'package:permission_handler/permission_handler.dart';
import 'package:dio/dio.dart';
import 'package:http_parser/http_parser.dart';
import 'package:wakelock_plus/wakelock_plus.dart';
// import 'package:vibration/vibration.dart';
import 'package:flutter/services.dart'; // for SystemSound
import 'package:path_provider/path_provider.dart';
import 'package:audioplayers/audioplayers.dart';
import 'package:web_socket_channel/io.dart'; // 移动端 WebSocket
import 'package:web_socket_channel/web_socket_channel.dart';

/// Injected at build time: flutter run --dart-define=OPENAI_API_KEY=sk-...
const String _openAiApiKey = String.fromEnvironment('OPENAI_API_KEY');

/// Main entry point of the application.
void main() {
  runApp(const VoiceApp());
}

/// Enum representing the various states of the app's lifecycle.
/// [guiding] 상태가 추가되었습니다 (손 유도 모드).
enum AppState { idle, listening, searching, guiding, success, fail }

/// 引导阶段：分阶段引导用户对齐
enum GuidanceStage {
  HORIZONTAL, // 左右对齐阶段
  DEPTH, // 前后接近阶段
  VERTICAL, // 上下对齐阶段
  READY_FOR_HAND // 准备伸手阶段
}

/// The root widget of the application.
class VoiceApp extends StatefulWidget {
  const VoiceApp({super.key});

  @override
  State<VoiceApp> createState() => _VoiceAppState();
}

class _VoiceAppState extends State<VoiceApp> {
  // ===============================================================
  // [1.dart] 기존 변수들 (VAD, WebSocket, TTS 등) - 변경 없음
  // ===============================================================
  AppState _state = AppState.idle;
  final FlutterTts _tts = FlutterTts();
  final AudioRecorder _record = AudioRecorder();
  final Dio _dio = Dio();

  late Map<String, String> fuzzyMap;
  late Set<String> keywords;

  Timer? _pollTimer;
  Completer<void>? _abortGuidance; // “중단 지도” 신호
  String _backendBase = 'http://192.168.0.110:8008';
  bool _foundAnnounced = false; // 일회성 가드: 중복 “찾음” 방지
  int _searchSessionId = 0;

  // === VAD 관련 ===
  StreamSubscription<Amplitude>? _amplitudeSubscription;
  Timer? _silenceTimer;
  String _currentRecordingPath = '';
  Timer? _maxRecordTimer;
  bool _isProcessing = false;
  WebSocketChannel? _wsChannel; // WebSocket 채널
  StreamSubscription? _wsSubscription; // WebSocket 리스너

  // —— VAD 튜닝 상수 ——
  static const double _enterGateDb = -42;
  static const double _keepGateDb = -45;
  static const Duration _silenceTimeout = Duration(milliseconds: 900);
  static const Duration _ampPeriod = Duration(milliseconds: 50);
  static const Duration _startGuard = Duration(milliseconds: 800);
  static const Duration _maxRecordTime = Duration(seconds: 6);
  static const Duration _noSpeechTimeout = Duration(seconds: 5);

  // —— 分阶段引导阈值 ——
  static const double _depthReadyThreshold = 0.2; // 前方准备阈值（0.8미터）
  static const double _alignmentThreshold = 0.1; // 水平/垂直对齐阈值
  static const double _objectAboveThreshold = 0.1; // 物体在上方判断阈值

  // —— VAD 状态变量 ——
  bool _speechStarted = false;
  int _voiceFrames = 0;
  DateTime? _lastLoud;
  DateTime? _recordStartAt;
  int _hotFrames = 0;

  // ===============================================================
  // [main.dart] 추가된 변수들 (TCP Server, IP, 손 유도)
  // ===============================================================
  ServerSocket? _tcpServer;
  final int _tcpPort = 6000;
  String _myIpDisplay = "IP 확인 중...";

  Timer? _repeatTimer; // 손 유도 반복 재생용 타이머
  Timer? _guidingTimeoutTimer; // 손 유도 모드 超时定时器 (60초)
  String _currentInstruction = ""; // 현재 재생 중인 손 유도 지시어
  GuidanceStage _guidanceStage = GuidanceStage.HORIZONTAL; // 当前引导阶段
  bool _handGuidanceCompleted = false; // 손 유도 완료 플래그 (반복 방지용)

// =======================================================
  // ★★★ [新增 1] 日志变量和辅助函数 ★★★
  // =======================================================
  final List<String> _logs = []; // 存储日志的列表
  final ScrollController _logScrollController = ScrollController();

  // 添加日志的函数
  void _addLog(String msg) {
    if (!mounted) return;
    setState(() {
      String time =
          DateTime.now().toString().substring(11, 19); // 获取时间 00:00:00
      _logs.insert(0, "[$time] $msg"); // 新日志插在最前面
      if (_logs.length > 100) _logs.removeLast(); // 最多保留100条
    });
  }

  @override
  void initState() {
    super.initState();

    // [1.dart] 기존 초기화 로직
    keywords = {'비빔면', '삼양라면', '사브레', '땅콩샌드', '버터링', '미트볼'};
    fuzzyMap = {
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
      '사벌레': '사브레',
      '사버리': '사브레',
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
    };

    _initializeTts().then((_) {
      if (mounted) {
        _speakWelcome();
      }
    });
    WakelockPlus.enable();
    _connectWebSocket();

    // [main.dart] 추가된 초기화 로직 (TCP 서버 시작 및 IP 확인)
    _startTcpServer();
    _checkMyIp();
  }

  @override
  void dispose() {
    // [main.dart] 추가 정리
    _tcpServer?.close();
    _repeatTimer?.cancel();
    _guidingTimeoutTimer?.cancel();

    // [1.dart] 기존 정리
    _pollTimer?.cancel();
    _tts.stop();
    _record.dispose();
    _amplitudeSubscription?.cancel();
    _silenceTimer?.cancel();
    try {
      _wsSubscription?.cancel();
    } catch (_) {}
    try {
      _wsChannel?.sink.close();
    } catch (_) {}

    super.dispose();
  }

  Future<void> _initializeTts() async {
    await _tts.setLanguage('ko-KR');
    await _tts.setSpeechRate(0.55);
    await _tts.setPitch(1.0);
    // [주의] 1.dart는 true, main.dart는 false였으나, 1.dart 로직 유지를 위해 true 유지
    await _tts.awaitSpeakCompletion(true);
  }

  // ===============================================================
  // [main.dart] 추가된 기능: TCP Server & IP Check
  // ===============================================================
  Future<void> _checkMyIp() async {
    try {
      for (var interface in await NetworkInterface.list()) {
        for (var addr in interface.addresses) {
          if (addr.type == InternetAddressType.IPv4 &&
              !addr.address.startsWith('127')) {
            setState(() {
              _myIpDisplay = "${addr.address} : $_tcpPort";
            });
          }
        }
      }
    } catch (_) {}
  }

  Future<void> _startTcpServer() async {
    _addLog("🌐 TCP 서버 시작 시도 (포트: $_tcpPort)");
    try {
      _tcpServer = await ServerSocket.bind(InternetAddress.anyIPv4, _tcpPort);
      print('>>>> TCP Server 시작: $_tcpPort');
      _addLog("✅ TCP 서버 시작 성공 (포트: $_tcpPort)");

      _tcpServer!.listen((Socket client) {
        print('>>>> HoloLens 연결됨: ${client.remoteAddress.address}');
        _addLog(
            "🔗 HoloLens 연결됨: ${client.remoteAddress.address}:${client.remotePort}");

        client
            .cast<List<int>>()
            .transform(utf8.decoder)
            .transform(const LineSplitter())
            .listen(
          (String data) {
            _handleTcpMessage(data);
          },
          onError: (e) {
            print('>>>> TCP Error: $e');
            _addLog("❌ TCP 에러: $e");
          },
          onDone: () {
            print('>>>> HoloLens 연결 끊김');
            _addLog("💔 HoloLens 연결 끊김");
          },
        );
      });
    } catch (e) {
      print('>>>> TCP 시작 실패: $e');
      _addLog("❌ TCP 서버 시작 실패: $e");
    }
  }

  // TCP 메시지 파싱 (main.dart 로직)
// [修改后的解析函数] 支持 JSON 格式和旧的字符串格式
  Map<String, dynamic> _parseStringData(String input) {
    Map<String, dynamic> result = {};
    String cleanInput = input.trim();

    // 1. 尝试作为 JSON 解析 (针对新的 HoloLens 数据格式)
    try {
      if (cleanInput.startsWith('{') && cleanInput.endsWith('}')) {
        final jsonMap = json.decode(cleanInput);

        final foundField = jsonMap['found'];
        // ✅ 0) 先解析 grabbed / finished（JSON 格式）
        final statusStr = (jsonMap['status'] ?? jsonMap['state'] ?? '')
            .toString()
            .toLowerCase();
        final msgStr = (jsonMap['message'] ?? jsonMap['msg'] ?? '')
            .toString()
            .toLowerCase();

        // 兼容：{"grabbed": true} 或 {"status":"grabbed"} 或 message里写 grabbed/finished
        if (jsonMap['grabbed'] == true ||
            statusStr == 'grabbed' ||
            statusStr == 'finished' ||
            msgStr.contains('grabbed') ||
            msgStr.contains('finished')) {
          result['status'] = 'grabbed';
          _addLog("✅ JSON解析: grabbed/finished -> status='grabbed'");
        }

        if (foundField == true) result['found'] = true;
        if (foundField == false) result['found'] = false; // 关键：锁死

        final msg = (jsonMap['message'] ?? '').toString().toLowerCase();
        // 只有在 found 字段不存在时，才用 message 兜底
        if (foundField == null &&
            msg.contains('found') &&
            !msg.contains('not found')) {
          result['found'] = true;
        }

        // 检查方向数据: 必须同时检查键存在和值大于阈值
        // 重要: HoloLens 可能同时发送多个方向键，只有值>0.1的才是有效方向

        _addLog("🧭 开始检查方向数据...");

        // 1. 水平方向 (Horizontal) - 互斥选择
        double leftVal =
            (jsonMap['left'] is num) ? jsonMap['left'].toDouble() : 0.0;
        double rightVal =
            (jsonMap['right'] is num) ? jsonMap['right'].toDouble() : 0.0;

        _addLog(
            "↔️ left原始=${jsonMap['left']} (${jsonMap['left'].runtimeType}) -> $leftVal");
        _addLog(
            "↔️ right原始=${jsonMap['right']} (${jsonMap['right'].runtimeType}) -> $rightVal");

        if (leftVal > 0.1) {
          result['horizontal'] = 'left';
          _addLog("✅ 设置 horizontal='left' (leftVal=$leftVal > 0.1)");
        } else if (rightVal > 0.1) {
          result['horizontal'] = 'right';
          _addLog("✅ 设置 horizontal='right' (rightVal=$rightVal > 0.1)");
        } else {
          _addLog("⚠️ 水平方向无有效值");
        }

        // 2. 垂直方向 (Vertical) - 互斥选择
        double upVal = (jsonMap['up'] is num) ? jsonMap['up'].toDouble() : 0.0;
        double topVal =
            (jsonMap['top'] is num) ? jsonMap['top'].toDouble() : 0.0;
        double downVal =
            (jsonMap['down'] is num) ? jsonMap['down'].toDouble() : 0.0;
        double bottomVal =
            (jsonMap['bottom'] is num) ? jsonMap['bottom'].toDouble() : 0.0;

        double upMax = upVal > topVal ? upVal : topVal;
        double downMax = downVal > bottomVal ? downVal : bottomVal;

        _addLog("↕️ up=$upVal, top=$topVal -> upMax=$upMax");
        _addLog("↕️ down=$downVal, bottom=$bottomVal -> downMax=$downMax");

        if (upMax > 0.1) {
          result['vertical'] = 'up';
          _addLog("✅ 设置 vertical='up' (upMax=$upMax > 0.1)");
        } else if (downMax > 0.1) {
          result['vertical'] = 'down';
          _addLog("✅ 设置 vertical='down' (downMax=$downMax > 0.1)");
        } else {
          _addLog("⚠️ 垂直方向无有效值");
        }

        // 3. 深度方向 - 使用depth字段（距离）配合阈值判断
        // 优先使用forward/backward字段（如果有），其次使用depth字段
        double forwardVal =
            (jsonMap['forward'] is num) ? jsonMap['forward'].toDouble() : 0.0;
        double backwardVal =
            (jsonMap['backward'] is num) ? jsonMap['backward'].toDouble() : 0.0;
        double backVal =
            (jsonMap['back'] is num) ? jsonMap['back'].toDouble() : 0.0;
        double depthVal =
            (jsonMap['depth'] is num) ? jsonMap['depth'].toDouble() : 0.0;

        double backMax = backwardVal > backVal ? backwardVal : backVal;

        _addLog(
            "⬆️⬇️ forward=$forwardVal, backward=$backwardVal, back=$backVal, depth=$depthVal");

        // 优先级1: 明确的forward/backward字段
        if (forwardVal > 0.1) {
          result['depth'] = 'forward';
          _addLog("✅ 设置 depth='forward' (forwardVal=$forwardVal > 0.1)");
        } else if (backMax > 0.1) {
          result['depth'] = 'backward';
          _addLog("✅ 设置 depth='backward' (backMax=$backMax > 0.1)");
        }
        // 优先级2: 使用depth字段（距离）配合阈值
        else if (depthVal > _depthReadyThreshold) {
          // depth > 阈值 → 距离远，需要往前
          result['depth'] = 'forward';
          _addLog(
              "✅ 设置 depth='forward' (depthVal=$depthVal > 阈值$_depthReadyThreshold)");
        } else if (depthVal > 0.01) {
          // 有depth值但在阈值内 → 已经够近，不需要往前
          _addLog(
              "👌 depth=$depthVal 在近距离范围内(< $_depthReadyThreshold)，不播报深度方向");
        } else {
          _addLog("⚠️ 无有效深度数据");
        }

        _addLog("📦 最终解析结果: $result");

        return result; // JSON 解析成功，直接返回
      }
    } catch (e) {
      // JSON 解析失败，继续尝试下面的正则解析
    }

    // 2. 旧的正则解析逻辑 (保留作为备份)
    String lowerInput = cleanInput.toLowerCase();
    if (lowerInput.contains('found') || lowerInput.contains('detected')) {
      result['found'] = true;
      return result;
    }
    if (lowerInput.contains('grabbed') || lowerInput.contains('finished')) {
      result['status'] = 'grabbed';
      return result;
    }

    final regex = RegExp(r'([a-z]+)\s*\(([\d\.]+)\)');
    final match = regex.firstMatch(lowerInput);

    if (match != null) {
      String direction = match.group(1) ?? '';
      if (direction == 'left' || direction == 'right') {
        result['horizontal'] = direction;
      } else if (direction == 'top' || direction == 'up') {
        result['vertical'] = 'up';
      } else if (direction == 'bottom' || direction == 'down') {
        result['vertical'] = 'down';
      } else if (direction == 'forward' || direction == 'front') {
        result['depth'] = 'forward';
      } else if (direction == 'backward' || direction == 'back') {
        result['depth'] = 'backward';
      }
    }
    return result;
  }

  // TCP 메시지 처리 (1.dart의 상태와 통합)
  void _handleTcpMessage(String rawData) async {
    _addLog("📥 TCP 원본 메시지 수신: $rawData");

    // searching 또는 guiding 상태일 때만 반응
    if (_state != AppState.searching && _state != AppState.guiding) {
      _addLog("⚠️ TCP 메시지 무시 (현재 상태: $_state)");
      return;
    }

    try {
      // 保存原始JSON（用于位置完美检测）
      Map<String, dynamic>? rawJson;
      String cleanInput = rawData.trim();
      if (cleanInput.startsWith('{') && cleanInput.endsWith('}')) {
        try {
          rawJson = json.decode(cleanInput);
        } catch (_) {}
      }

      final data = _parseStringData(rawData);
      _addLog("🔍 TCP 파싱 결과: $data");

      // --- 단계 1: 검색 중 (Searching) ---
      if (_state == AppState.searching) {
        // TCP를 통해 'found' 신호가 오면, "찾았습니다"를 말한 뒤 손 유도 모드로 전환
        if (!_foundAnnounced && data['found'] == true) {
          print(">>>> TCP: 물체 발견 신호 수신 -> '찾았습니다' 안내 후 손 유도 모드 전환");
          _addLog("🎯 TCP: 물체 발견 신호! -> 손 유도 모드 전환");
          await _announceFoundThenStartHandGuidance();
        } else {
          _addLog("📊 TCP: Searching 상태에서 메시지 수신했으나 found=false");
        }
        return;
      }

      // --- 단계 2: 손 유도 중 (Guiding) ---
      if (_state == AppState.guiding) {
        // ★★★ 최우선: Unity가 found 메시지를 보내면 완료 처리 ★★★
        // ✅ 1) grabbed 最优先：真正“抓取完成”
        if (data['status'] == 'grabbed') {
          _addLog("👋 TCP: Grabbed 신호 수신! -> 성공 처리");
          _guidingTimeoutTimer?.cancel();
          await _onSuccess();
          return;
        }
        // ✅ 2) found 次优先：Unity的“找到”信号
        // [已移除] found 连续信号会导致引导秒退，只有 grabbed 才是真正的结束
        /*
        if (data['found'] == true) {
          _addLog("🎯 Unity에서 'found' 수신 (guiding 중) -> 완료 안내 후 idle로 복귀");

          // 모든 작업 중단 (包括超时定时器)
          _pollTimer?.cancel();
          _repeatTimer?.cancel();
          _guidingTimeoutTimer?.cancel();
          _handGuidanceCompleted = true;

          try {
            if (!(_abortGuidance?.isCompleted ?? true)) {
              _abortGuidance?.complete();
            }
          } catch (_) {}

          try {
            await _tts.stop();
          } catch (_) {}

          // 완료 안내
          await _tts.speak('찾았습니다');
          _addLog("🗣️ TTS: '찾았습니다' (손 유도 완료)");

          // idle 상태로 복귀
          await _returnToIdle();
          return;
        }
        */

        // ✅ 완료 플래그 체크
        if (_handGuidanceCompleted) {
          _addLog("✅ 손 유도 이미 완료됨 - 메시지 무시");
          return;
        }

        // 분阶段引导系统
        if (rawJson != null) {
          GuidanceStage newStage = _determineGuidanceStage(rawJson);

          // 更新阶段
          if (newStage != _guidanceStage) {
            _addLog("🔄 阶段转换: $_guidanceStage → $newStage");
            _guidanceStage = newStage;
          }
        }

        // 位置 데이터가 오면 안내 음성 처리
        _addLog("📍 TCP: 위치 데이터 수신 -> 손 유도 처리");
        await _processHandGuidance(data, rawJson);
      }
    } catch (e) {
      print(">>>> TCP 파싱 에러: $e");
      _addLog("❌ TCP 파싱 에러: $e (원본: $rawData)");
    }
  }

  // 判断当前应该处于哪个引导阶段 (优先级: 左右 -> 上下 -> 前后)
  GuidanceStage _determineGuidanceStage(Map<String, dynamic> jsonData) {
    // 提取所有方向值
    double leftVal =
        (jsonData['left'] is num) ? jsonData['left'].toDouble() : 0.0;
    double rightVal =
        (jsonData['right'] is num) ? jsonData['right'].toDouble() : 0.0;
    double topVal = (jsonData['top'] is num) ? jsonData['top'].toDouble() : 0.0;
    double bottomVal =
        (jsonData['bottom'] is num) ? jsonData['bottom'].toDouble() : 0.0;
    double depthVal =
        (jsonData['depth'] is num) ? jsonData['depth'].toDouble() : 0.0;

    double horizontalMax = leftVal > rightVal ? leftVal : rightVal;
    double verticalMax = topVal > bottomVal ? topVal : bottomVal;

    _addLog("🔍 阶段判断: H=$horizontalMax, V=$verticalMax, D=$depthVal");

    // 阶段1: 左右对齐 (最高优先级)
    if (horizontalMax > _alignmentThreshold) {
      _addLog("➡️ 阶段: HORIZONTAL (左右对齐)");
      return GuidanceStage.HORIZONTAL;
    }

    // 阶段2: 上下对齐
    if (verticalMax > _alignmentThreshold) {
      _addLog("↕️ 阶段: VERTICAL (上下对齐)");
      return GuidanceStage.VERTICAL;
    }

    // 阶段3: 前后接近
    if (depthVal > _depthReadyThreshold) {
      _addLog("⬆️ 阶段: DEPTH (前后接近)");
      return GuidanceStage.DEPTH;
    }

    // 所有方向都已对齐，保持当前阶段，继续等待Unity的found消息
    _addLog("✅ 所有方向已对齐，等待Unity found消息");
    return _guidanceStage; // 保持当前阶段
  }

  // [main.dart 기능] 손 유도 모드로 전환
  Future<void> _switchToHandGuidance() async {
    // 1.dart의 기존 검색 관련 작업들(폴링, TTS 루프)을 모두 중단해야 함
    _foundAnnounced = true; // 웹소켓/폴링이 중복 처리하지 않도록 설정
    _pollTimer?.cancel();
    _pollTimer = null;

    try {
      if (!(_abortGuidance?.isCompleted ?? true)) {
        _abortGuidance?.complete(); // 고개 돌리기 안내 중단
      }
    } catch (_) {}

    try {
      await _tts.stop();
    } catch (_) {}

    setState(() {
      _state = AppState.guiding;
    });

    _currentInstruction = "";
    _repeatTimer?.cancel();

    // "찾았습니다. 손을 움직여주세요."
    await _tts.speak('찾았습니다. 손을 움직여주세요.');
  }

  // [요구사항] 검색(고개 유도 루프) 도중 'found'가 들어오면:
  // 1) "찾았습니다."를 먼저 끝까지 말하고
  // 2) 그 다음에 guiding(손 유도) 상태로 전환하여 방향 안내를 받습니다.
  Future<void> _announceFoundThenStartHandGuidance() async {
    // 중복 방지
    if (_foundAnnounced) return;
    _addLog("🎯 손 유도 모드로 전환");
    if (!mounted) return;
    if (_state != AppState.searching) return;

    _foundAnnounced = true;

    // 검색 관련 작업 즉시 중단 (폴링, 고개 유도)
    _pollTimer?.cancel();
    _pollTimer = null;

    try {
      if (!(_abortGuidance?.isCompleted ?? true)) {
        _abortGuidance?.complete(); // 고개 유도 루프 중단
      }
    } catch (_) {}

    // 손 유도 반복 타이머는 초기화 (guiding 전환 시 다시 사용)
    _repeatTimer?.cancel();
    _repeatTimer = null;

    // 현재 말하는 중이라면 끊고, "찾았습니다"를 먼저 말함
    try {
      await _tts.stop();
    } catch (_) {}

    // 1) 찾았다는 안내 (완전히 말한 뒤 다음 단계로)
    await _tts.speak('찾았습니다.');
    _addLog("🗣️ TTS: '찾았습니다.'");

    if (!mounted) return;

    // 2) 손 유도 모드로 전환 (이 시점부터 TCP 방향 메시지를 받으면 안내 시작)
    setState(() {
      _state = AppState.guiding;
    });
    _addLog("🔄 상태 변경: AppState.guiding");
    _guidanceStage = GuidanceStage.HORIZONTAL;
    _handGuidanceCompleted = false;
    _currentInstruction = "";

    _repeatTimer?.cancel();

    // 播报开始引导
    await _tts.speak('음성 안내를 시작합니다.');
    _addLog("🗣️ TTS: '음성 안내를 시작합니다.'");

    // ★★★ 启动60秒超时定时器 ★★★
    _guidingTimeoutTimer?.cancel();
    _guidingTimeoutTimer = Timer(const Duration(seconds: 60), () async {
      if (!mounted) return;
      if (_state != AppState.guiding) return;

      _addLog("⏱️ 손 유도 모드 超时 (60초) - 自动退出");

      try {
        await _tts.stop();
      } catch (_) {}

      await _tts.speak('시간 초과입니다.');
      await _returnToIdle();
    });
    _addLog("⏱️ 손 유도 超时定时器已启动 (60초)");

    // 移除定时器重复播报逻辑：改为根据位置数据变化智能播报
    // TTS 现在由 _processHandGuidance 管理，只在方向改变时播报
  }

  // [개선된] 손 위치 안내 텍스트 생성 - 分阶段引导
  String _generateHandText(Map<String, dynamic> data, GuidanceStage stage) {
    String? horizontal = data['horizontal'];
    String? vertical = data['vertical'];
    String? depth = data['depth'];

    // 根据阶段过滤方向
    switch (stage) {
      case GuidanceStage.HORIZONTAL:
        // 只返回左右方向
        if (horizontal == 'left') return '왼쪽';
        if (horizontal == 'right') return '오른쪽';
        return '';

      case GuidanceStage.DEPTH:
        // 只返回前后方向
        if (depth == 'forward') return '앞으로';
        if (depth == 'backward') return '뒤로';
        return '';

      case GuidanceStage.VERTICAL:
        // 只返回上下方向
        if (vertical == 'up') return '위';
        if (vertical == 'down') return '아래';
        return '';

      case GuidanceStage.READY_FOR_HAND:
        // 准备伸手，不返回方向
        return '';
    }
  }

  // 判断是否需要立即打断（方向反转）
  bool _shouldInterrupt(String oldInstr, String newInstr) {
    // 空指令不打断
    if (oldInstr.isEmpty || newInstr.isEmpty) return false;

    // 检查是否有相反方向的词
    bool hasOpposite = false;

    // 左 ↔ 右
    if ((oldInstr.contains('왼쪽') && newInstr.contains('오른쪽')) ||
        (oldInstr.contains('오른쪽') && newInstr.contains('왼쪽'))) {
      hasOpposite = true;
    }

    // 上 ↔ 下
    if ((oldInstr.contains('위') && newInstr.contains('아래')) ||
        (oldInstr.contains('아래') && newInstr.contains('위'))) {
      hasOpposite = true;
    }

    // 前 ↔ 后
    if ((oldInstr.contains('앞으로') && newInstr.contains('뒤로')) ||
        (oldInstr.contains('뒤로') && newInstr.contains('앞으로'))) {
      hasOpposite = true;
    }

    return hasOpposite;
  }

  // 손 유도 완료 후 idle 상태로 복귀
  Future<void> _returnToIdle() async {
    _addLog("🏠 손 유도 완료 - idle 상태로 복귀");

    // TTS 중지
    try {
      await _tts.stop();
    } catch (_) {}

    // 타이머 정리
    _repeatTimer?.cancel();
    _repeatTimer = null;
    _guidingTimeoutTimer?.cancel();
    _guidingTimeoutTimer = null;

    // 상태 초기化
    _currentInstruction = "";
    _guidanceStage = GuidanceStage.HORIZONTAL;

    // 다음 검색을 위해 플래그 초기化
    _foundAnnounced = false;
    _handGuidanceCompleted = false; // ✅ 修复3: 重置手部引导完成标志

    // idle 상태로 전환
    if (mounted) {
      setState(() {
        _state = AppState.idle;
        _addLog("🔄 상태 변경: AppState.idle - 다음 검색 준비 완료");
      });
    }
  }

  // [개선된] 손 안내 음성 처리 - 스마트 TTS 관리
  bool _isSpeaking = false; // TTS 정在播报
  Future<void> _processHandGuidance(
      Map<String, dynamic> data, Map<String, dynamic>? rawJson) async {
    String newInstruction = _generateHandText(data, _guidanceStage);
    _addLog("✋ 손 유도 데이터 처리: $data -> '$newInstruction'");

    // 移除指令相同跳过逻辑，允许持续引导
    // 即使方向相同也继续播报，帮助用户持续调整

    // 检查是否需要立即打断
    bool shouldInterrupt =
        _shouldInterrupt(_currentInstruction, newInstruction);

    if (shouldInterrupt) {
      // 方向反转，立即停止当前语音
      _addLog("⚠️ 方向反转，立即打断: '$_currentInstruction' -> '$newInstruction'");
      await _tts.stop();
      _isSpeaking = false;
    } else if (_isSpeaking) {
      // 不是反转，但正在说话，等待说完
      _addLog("⏳ 等待当前语音播报完成: '$_currentInstruction'");
      // 更新指令但不立即播报
      _currentInstruction = newInstruction;
      return;
    }

    // 更新指令
    setState(() {
      _currentInstruction = newInstruction;
    });

    // 如果新指令为空，停止播报
    if (newInstruction.isEmpty) {
      _addLog("✅ 位置正确，停止指令");
      await _tts.stop();
      _isSpeaking = false;
      return;
    }

    // 播报新指令
    _addLog("🗣️ 播报指令: '$newInstruction'");
    _isSpeaking = true;

    try {
      await _tts.speak(newInstruction);
    } catch (e) {
      _addLog("❌ TTS 播报错误: $e");
    } finally {
      _isSpeaking = false;
      _addLog("✅ 播报完成");

      // 关键：播报完成后，检查指令是否在播报期间被更新过
      // 如果 _currentInstruction 与我们刚说的不同，说明有新指令进来需要播报
      if (_currentInstruction != newInstruction && _state == AppState.guiding) {
        String latestInstruction = _currentInstruction;
        _addLog("🔄 播报期间指令已更新为: '$latestInstruction'，立即播报");

        // 直接播报最新指令（不递归调用，避免复杂性）
        _isSpeaking = true;
        try {
          await _tts.speak(latestInstruction);
        } catch (e) {
          _addLog("❌ TTS 续播错误: $e");
        } finally {
          _isSpeaking = false;
          _addLog("✅ 续播完成");

          // 再次检查：说完这句后，指令是否又变了
          // 注意：这里不再递归，避免无限循环
          // 如果还有更新，下次TCP消息会触发新的播报
        }
      }
    }
  }

  // Helper method to check if the backend payload indicates "found"
// 请把这个函数放在 _VoiceAppState 类里，替代以前的逻辑
  bool isFoundPayload(dynamic data) {
    try {
      String toText(dynamic v) => (v ?? '').toString().toLowerCase();

      // 定义：只有这些词出现才算真正成功
      bool containsSuccessWords(String t) {
        // 排除 "not found" 的情况！！！
        if (t.contains('not found')) return false;
        if (t.contains('fail')) return false;

        // 必须是明确的成功词
        if (t.contains('success') ||
            t.contains('completed') ||
            t.contains('finished') ||
            t.contains('찾았습니다') ||
            t.contains('성공') ||
            t.contains('완료')) return true;

        // 对于 'found'，我们要小心，确保前面没有 'not'
        if (t.contains('found') && !t.contains('not found')) return true;

        return false;
      }

      if (data is Map) {
        // 1. 最优先：相信布尔值 (Boolean)
        // 只要 found 是 false，就绝不认为是成功，哪怕 message 里有 found 单词
        if (data['found'] == false) return false;
        if (data['found'] == true || data['success'] == true) return true;

        // 2. 检查有没有高亮框 (Highlight) - 这是最硬的证据
        if (data.containsKey('highlight') ||
            data.containsKey('bbox') ||
            toText(data['message']).contains('highlight')) {
          return true;
        }

        // 3. 最后才检查文字消息
        final statusStr = toText(data['status']);
        final msgStr = toText(data['message'] ?? data['msg']);
        final combined = '$statusStr $msgStr';

        return containsSuccessWords(combined);
      } else if (data is List) {
        for (final item in data) {
          if (isFoundPayload(item)) return true;
        }
        return false;
      } else {
        // 纯字符串情况
        final t = toText(data);
        return containsSuccessWords(t);
      }
    } catch (_) {
      return false;
    }
  }

  // ===============================================================
  // [1.dart] 기존 로직 (flush, welcome, prompt 등)
  // ===============================================================
  Future<void> _flushAndResetSession() async {
    _addLog("🔄 세션 초기화 시작");
    // [통합] 손 유도 타이머도 취소
    _repeatTimer?.cancel();

    // 기존 로직
    try {
      _abortGuidance?.complete();
    } catch (_) {}
    _abortGuidance = null;
    _pollTimer?.cancel();
    _pollTimer = null;
    try {
      await _tts.stop();
    } catch (_) {}
    await _amplitudeSubscription?.cancel();
    _amplitudeSubscription = null;
    _silenceTimer?.cancel();
    _silenceTimer = null;
    _maxRecordTimer?.cancel();
    _maxRecordTimer = null;
  }

  Future<void> _speakWelcome() async {
    _addLog("👋 환영 메시지 재생");
    setState(() {
      _state = AppState.idle;
      _addLog("🔄 상태 변경: AppState.idle");
    });
    await _tts.speak('지능형 음성 입력 시스템에 오신 것을 환영합니다.');
  }

  void _onTap() {
    if (_state == AppState.idle) {
      _promptForInput();
    }
  }

  Future<void> _promptForInput() async {
    _addLog("🎤 음성 입력 대기 시작");
    await _flushAndResetSession();
    setState(() {
      _state = AppState.listening;
      _addLog("🔄 상태 변경: AppState.listening");
    });
    // 双用途提示：搜索或确认
    await _tts.speak('물품을 찾거나 확인하세요');
    await _startRecording();
  }

  // ===============================================================
  // [1.dart] VAD 및 녹음 로직 (변동 없음)
  // ===============================================================
  Future<void> _startRecording() async {
    _addLog("🎙️ 녹음 시작 시도");
    if (!await _record.hasPermission()) {
      await _tts.speak('마이크 권한이 필요합니다.');
      _addLog("❌ 마이크 권한 없음");
      setState(() {
        _state = AppState.idle;
        _addLog("🔄 상태 변경: AppState.idle");
      });
      return;
    }
    _maxRecordTimer?.cancel();
    _silenceTimer?.cancel();
    _amplitudeSubscription?.cancel();

    _speechStarted = false;
    _lastLoud = null;
    _hotFrames = 0;

    final tempDir = await getTemporaryDirectory();
    _currentRecordingPath =
        '${tempDir.path}/input_${DateTime.now().millisecondsSinceEpoch}.m4a';

    try {
      await _record.start(
        const RecordConfig(
          encoder: AudioEncoder.aacLc,
          bitRate: 128000,
          sampleRate: 16000,
          numChannels: 1,
        ),
        path: _currentRecordingPath,
      );
      _addLog("✅ 녹음 시작 성공: $_currentRecordingPath");
    } catch (e) {
      print(">>>> 录音启动失败: $e");
      _addLog("❌ 녹음 시작 실패: $e");
      setState(() {
        _state = AppState.idle;
        _addLog("🔄 상태 변경: AppState.idle");
      });
      await _tts.speak('녹음을 시작할 수 없습니다.');
      return;
    }

    _recordStartAt = DateTime.now();
    _startVad();

    _maxRecordTimer = Timer(_maxRecordTime, () async {
      print(">>>> VAD: 触发最长录音时间 (${_maxRecordTime.inSeconds}s)，停止");
      _addLog("⏱️ VAD: 최대 녹음 시간 초과 (${_maxRecordTime.inSeconds}s)");
      if (await _record.isRecording()) {
        await _stopRecordingAndProcess(
            triggeredByTimeoutWithoutSpeech: !_speechStarted);
      }
    });
  }

  void _startVad() {
    _addLog("🔊 VAD 시작");
    _amplitudeSubscription?.cancel();
    _speechStarted = false;
    _lastLoud = null;
    _hotFrames = 0;
    int quietFrames = 0;

    _amplitudeSubscription =
        _record.onAmplitudeChanged(_ampPeriod).listen((amp) async {
      if (!await _record.isRecording()) {
        _amplitudeSubscription?.cancel();
        _maxRecordTimer?.cancel();
        return;
      }

      final now = DateTime.now();
      final double db = amp.current;

      if (_recordStartAt == null ||
          now.difference(_recordStartAt!) < _startGuard) {
        return;
      }

      if (!_speechStarted) {
        if (db > _enterGateDb) {
          _hotFrames++;
          if (_hotFrames >= 3) {
            print(">>>> VAD: 检测到语音开始 (阈值 ${_enterGateDb}dB)");
            _addLog("🗣️ 음성 감지 시작 (${_enterGateDb}dB)");
            _speechStarted = true;
            _lastLoud = now;
            quietFrames = 0;
          }
        } else {
          _hotFrames = 0;
        }

        if (!_speechStarted &&
            now.difference(_recordStartAt!) > _noSpeechTimeout) {
          print(">>>> VAD: ${_noSpeechTimeout.inMilliseconds}ms 仍未检测到语音开始，停止");
          _addLog("🚫 VAD: 음성 감지 시간 초과 (${_noSpeechTimeout.inMilliseconds}ms)");
          if (await _record.isRecording()) {
            await _stopRecordingAndProcess(
                triggeredByTimeoutWithoutSpeech: true);
          }
          return;
        }
      } else {
        if (db > _keepGateDb) {
          _lastLoud = now;
          quietFrames = 0;
        } else {
          quietFrames++;
        }

        if (quietFrames >= 8) {
          print(">>>> VAD: 检测到快速收口 (${quietFrames}帧安静)，停止");
          _addLog("🔇 VAD: 음성 종료 감지 (${quietFrames}프레임 침묵)");
          if (await _record.isRecording()) {
            await _stopRecordingAndProcess();
          }
          return;
        }

        if (_lastLoud != null && now.difference(_lastLoud!) > _silenceTimeout) {
          print(">>>> VAD: 检测到兜底静音超时 (${_silenceTimeout.inMilliseconds}ms)，停止");
          _addLog("⏳ VAD: 침묵 시간 초과 (${_silenceTimeout.inMilliseconds}ms)");
          if (await _record.isRecording()) {
            await _stopRecordingAndProcess();
          }
          return;
        }
      }
    });
  }

  Future<void> _stopRecordingAndProcess(
      {bool triggeredByTimeoutWithoutSpeech = false}) async {
    if (_isProcessing) {
      print(">>>> 已在处理流程中，跳过重复调用");
      _addLog("⚠️ 이미 처리 중, 중복 호출 무시");
      return;
    }
    _isProcessing = true;
    try {
      if (_state != AppState.listening) {
        print(">>>> VAD: 试图停止，但当前状态不是 listening ($_state)，忽略");
        _addLog("🚫 VAD: 현재 상태가 listening이 아님 ($_state), 녹음 중지 무시");
        _amplitudeSubscription?.cancel();
        _maxRecordTimer?.cancel();
        _amplitudeSubscription = null;
        _maxRecordTimer = null;
        return;
      }

      print(
          ">>>> VAD: _stopRecordingAndProcess 开始 (超时无语音: $triggeredByTimeoutWithoutSpeech)");
      _addLog(
          "🛑 VAD: 녹음 중지 및 처리 시작 (타임아웃/무음성: $triggeredByTimeoutWithoutSpeech)");

      await _amplitudeSubscription?.cancel();
      _maxRecordTimer?.cancel();
      _amplitudeSubscription = null;
      _maxRecordTimer = null;
      final bool hadSpeech = _speechStarted;
      _speechStarted = false;

      bool wasRecording = false;
      try {
        wasRecording = await _record.isRecording();
      } catch (e) {
        print(">>>> VAD: 检查 isRecording 时出错: $e");
        _addLog("❌ VAD: isRecording 확인 중 에러: $e");
        setState(() {
          _state = AppState.idle;
          _addLog("🔄 상태 변경: AppState.idle");
        });
        return;
      }

      if (!wasRecording) {
        print(">>>> VAD: 试图停止时已经不在录音，忽略");
        _addLog("🚫 VAD: 이미 녹음 중이 아님, 중지 무시");
        if (_state != AppState.idle && _state != AppState.success) {
          setState(() {
            _state = AppState.idle;
            _addLog("🔄 상태 변경: AppState.idle");
          });
        }
        return;
      }

      String? savedPath;
      try {
        savedPath = await _record.stop();
        _addLog("✅ 녹음 중지 성공");
      } catch (e) {
        print(">>>> VAD: 调用 stop() 时出错: $e");
        _addLog("❌ VAD: 녹음 stop() 에러: $e");
        setState(() {
          _state = AppState.idle;
          _addLog("🔄 상태 변경: AppState.idle");
        });
        await _tts.speak('녹음 중지 오류.');
        return;
      }

      final audioPath = savedPath ?? _currentRecordingPath;

      if (triggeredByTimeoutWithoutSpeech || !hadSpeech) {
        print(">>>> VAD: 超时/无语音，按未识别处理");
        _addLog("⚠️ VAD: 타임아웃 또는 음성 없음, 미인식 처리");
        try {
          if (audioPath.isNotEmpty) {
            final file = File(audioPath);
            if (await file.exists() && await file.length() < 1024) {
              await file.delete();
              _addLog("🗑️ 작은 오디오 파일 삭제: $audioPath");
            }
          }
        } catch (e) {
          print(">>>> 清理失败: $e");
          _addLog("❌ 오디오 파일 정리 실패: $e");
        }
        await _handleUnrecognized();
        return;
      }

      if (audioPath.isEmpty) {
        _addLog("⚠️ 오디오 파일 경로 없음, 미인식 처리");
        await _handleUnrecognized();
        return;
      }

      try {
        final file = File(audioPath);
        if (!await file.exists() || await file.length() < 1024) {
          _addLog("⚠️ 오디오 파일 유효하지 않음 (없거나 너무 작음), 미인식 처리");
          if (await file.exists()) await file.delete();
          await _handleUnrecognized();
          return;
        }
      } catch (e) {
        _addLog("❌ 오디오 파일 유효성 검사 중 에러: $e, 미인식 처리");
        await _handleUnrecognized();
        return;
      }

      await _processAudioFile(audioPath);

      try {
        final file = File(audioPath);
        if (await file.exists()) {
          await file.delete();
          print(">>>> 清理已处理文件: $audioPath");
          _addLog("🗑️ 처리된 오디오 파일 삭제: $audioPath");
        }
      } catch (e) {
        print(">>>> 清理已处理文件失败: $e");
        _addLog("❌ 처리된 오디오 파일 삭제 실패: $e");
      }
    } finally {
      _isProcessing = false;
      _addLog("✅ 녹음 중지 및 처리 완료");
    }
  }

  // ===============================================================
  // [1.dart] API 통신 로직 (Whisper, Backend, WebSocket)
  // ===============================================================
  Future<void> _processAudioFile(String filePath) async {
    _addLog("📤 오디오 파일 처리 시작: $filePath");

    try {
      final transcript = await _transcribeAudio(filePath);
      print('>>>> Whisper API 返回的原始文本: $transcript');
      _addLog("📝 Whisper 인식: $transcript");

      if (transcript == null || transcript.isEmpty) {
        _addLog("⚠️ Whisper 인식 결과 없음");
        await _handleUnrecognized();
        return;
      }

      final cleaned = _normalizeText(transcript);
      _addLog("🧹 정규화된 텍스트: $cleaned");

      // ===== 🔀 分支判断：确认 vs 搜索 =====

      // 优先检查：확인 분지
      if (_isConfirmQuery(cleaned)) {
        final keyword = _extractConfirmKeyword(cleaned);
        if (keyword != null) {
          _addLog("➡️ 确认分支: $keyword");
          await _handleConfirmQuery(keyword);
          return;
        } else {
          _addLog("⚠️ 确认模式但无法提取关键词");
        }
      }

      // 其次检查：검색 분지（需要有搜索触发词）
      if (_isSearchQuery(cleaned)) {
        _addLog("➡️ 搜索分支");
        await _flushAndResetSession();
        setState(() {
          _state = AppState.searching;
          _addLog("🔄 상태 변경: AppState.searching");
        });
        await _tts.speak('처리 중입니다.');

        final canonical = _matchKeyword(cleaned);
        _addLog("🔍 키워드 매칭: $canonical");

        if (canonical == null) {
          _addLog("❌ 키워드 매칭 실패");
          await _handleUnrecognized();
        } else {
          final phrase = '$canonical 찾아줘';
          _addLog("🚀 백엔드 전송 문구: $phrase");
          await _sendToBackend(phrase);
        }
        return;
      }

      // 都不匹配：提示用户重新说
      _addLog("❌ 无法识别指令类型");
      await _handleUnrecognized();
    } catch (e) {
      _addLog("❌ 오디오 파일 처리 중 에러: $e");
      await _tts.speak('죄송합니다. 오류가 발생했습니다.');
      setState(() {
        _state = AppState.idle;
        _addLog("🔄 상태 변경: AppState.idle");
      });
    }
  }

  String _normalizeText(String input) {
    var text = input.trim().toLowerCase();
    text = text.replaceAll(RegExp(r'[^\uAC00-\uD7AF\s]'), '');
    const particles = [
      '은',
      '는',
      '이',
      '가',
      '을',
      '를',
      '에',
      '에서',
      '과',
      '와',
      '도',
      '로',
      '으로',
      '의'
    ];
    for (final p in particles) {
      text = text.replaceAll(p, '');
    }
    text = text.replaceAll(RegExp(r'\s+'), '');
    return text;
  }

  String _squash(String s) => s.replaceAll(RegExp(r'\s+'), '');

  String? _matchKeyword(String text) {
    final t = _squash(text);
    for (final kw in keywords) {
      if (t.contains(_squash(kw))) {
        return kw;
      }
    }
    for (final entry in fuzzyMap.entries) {
      if (t.contains(_squash(entry.key))) {
        return entry.value;
      }
    }
    return null;
  }

  // ===============================================================
  // [新增] 确认查询检测函数
  // ===============================================================

  /// 检测是否为确认查询
  /// 触发词：맞나요/입니까/인가요/이거/이것은/이게/맞아
  bool _isConfirmQuery(String text) {
    final confirmTriggers = [
      '맞나요',
      '맞습니까',
      '입니까',
      '인가요',
      '이거',
      '이것은',
      '이게',
      '맞아',
      '맞아요',
    ];
    return confirmTriggers.any((trigger) => text.contains(trigger));
  }

  /// 检测是否为搜索查询
  /// 触发词：찾아/주세요/어디
  bool _isSearchQuery(String text) {
    final searchTriggers = [
      '찾아',
      '찾아줘',
      '찾아주세요',
      '어디',
      '어디에',
      '어디있어',
      '주세요',
      '줘',
    ];
    return searchTriggers.any((trigger) => text.contains(trigger));
  }

  /// 从确认查询中提取关键词
  String? _extractConfirmKeyword(String text) {
    // 移除确认触发词
    String cleaned = text
        .replaceAll('이거', '')
        .replaceAll('이것은', '')
        .replaceAll('이게', '')
        .replaceAll('맞나요', '')
        .replaceAll('맞습니까', '')
        .replaceAll('입니까', '')
        .replaceAll('인가요', '')
        .replaceAll('맞아', '')
        .replaceAll('맞아요', '')
        .trim();

    _addLog("🔍 确认模式关键词提取: '$text' -> '$cleaned'");
    return _matchKeyword(cleaned);
  }

  Future<String?> _transcribeAudio(String filePath) async {
    _addLog("🎙️ Whisper API 호출 시작");
    try {
      final file = await MultipartFile.fromFile(
        filePath,
        filename: 'audio.m4a',
        contentType: MediaType('audio', 'm4a'),
      );
      final formData = FormData.fromMap({
        'file': file,
        'language': 'ko',
        'model': 'whisper-1',
      });

      final response = await _dio.post(
        'https://api.openai.com/v1/audio/transcriptions',
        data: formData,
        options: Options(
          headers: {
            'Authorization': 'Bearer $_openAiApiKey',
          },
        ),
      );
      _addLog("✅ Whisper API 응답 수신");
      return response.data['text'] as String;
    } catch (e) {
      print('Whisper API Error: $e');
      _addLog("❌ Whisper API 에러: $e");
      return null;
    }
  }

  Future<void> _handleUnrecognized() async {
    _addLog("❌ 인식 실패 - 재시도 요청");
    await _flushAndResetSession();
    setState(() {
      _state = AppState.listening;
      _addLog("🔄 상태 변경: AppState.listening");
    });
    await _tts.speak('죄송합니다. 잘 못 들었습니다. 다시 말씀해 주세요.');
    await _startRecording();
  }

  // ===============================================================
  // [新增] 确认查询处理函数（5秒实时检测）
  // ===============================================================

  /// 处理确认查询
  Future<void> _handleConfirmQuery(String keyword) async {
    _addLog("🔍 确认查询开始: $keyword");

    setState(() {
      _state = AppState.searching; // 复用searching状态显示处理中
      _addLog("🔄 상태 변경: AppState.searching (확인 중)");
    });

    // 播报：确认中，请稍等（因为需要等待5秒）
    await _tts.speak('확인 중입니다. 잠시만 기다려 주세요');
    _addLog("🗣️ TTS: '확인 중입니다. 잠시만 기다려 주세요'");

    try {
      final uri = Uri.parse(_backendBase);
      final confirmUrl = 'http://${uri.host}:8008/confirm';
      _addLog("🌐 确认 URL: $confirmUrl");

      // 发送请求，Python后端会在5秒内实时检测OCR
      final response = await _dio.post(
        confirmUrl,
        data: {'keyword': keyword},
        options: Options(
          headers: {'Content-Type': 'application/json'},
          sendTimeout: const Duration(seconds: 7), // 稍长于5秒
          receiveTimeout: const Duration(seconds: 7),
        ),
      );

      _addLog("📥 确认响应: ${response.data}");

      final isMatch = response.data['is_match'] ?? false;

      if (isMatch) {
        await _tts.speak('네, 맞습니다');
        _addLog("✅ 确认结果: 匹配 ($keyword)");
      } else {
        await _tts.speak('아니요, 다릅니다');
        _addLog("❌ 确认结果: 不匹配 ($keyword)");
      }

      // 等待TTS完成
      await Future.delayed(const Duration(milliseconds: 2000));

      // 返回idle状态
      await _flushAndResetSession();
      setState(() {
        _state = AppState.idle;
        _addLog("🔄 상태 변경: AppState.idle");
      });
    } catch (e) {
      _addLog("❌ 确认请求失败: $e");
      await _tts.speak('확인할 수 없습니다');

      await Future.delayed(const Duration(milliseconds: 1500));

      setState(() {
        _state = AppState.idle;
        _addLog("🔄 상태 변경: AppState.idle");
      });
    }
  }

  Future<void> _sendToBackend(String phrase) async {
    _addLog("📡 백엔드로 전송: $phrase");
    try {
      _abortGuidance?.complete();
    } catch (_) {}
    _abortGuidance = Completer<void>();
    _pollTimer?.cancel();
    _pollTimer = null;
    try {
      await _tts.stop();
    } catch (_) {}
    _foundAnnounced = false;
    final int sessionId = ++_searchSessionId;

    setState(() {
      _state = AppState.searching;
      _addLog("🔄 상태 변경: AppState.searching");
    });
    await _tts.speak('찾는 중입니다.');

    // 고개 지도 루프 시작 (1.dart 방식)
    _startGuidanceTTSLoop();

    try {
      await _dio.post(
        '$_backendBase/voice',
        data: {'text': phrase},
        options: Options(
          headers: {'Content-Type': 'application/json'},
          sendTimeout: const Duration(seconds: 5),
          receiveTimeout: const Duration(seconds: 5),
        ),
      );
      _addLog("✅ 백엔드 요청 성공");
    } catch (e) {
      _addLog("❌ 백엔드 요청 실패: $e");
      await _tts.speak('서버에 연결할 수 없습니다.');
      setState(() {
        _state = AppState.idle;
        _addLog("🔄 상태 변경: AppState.idle");
      });
      if (!(_abortGuidance?.isCompleted ?? true)) _abortGuidance?.complete();
      return;
    }

    _startContinuousPolling(sessionId);
  }

  void _connectWebSocket() {
    if (_wsChannel != null) return;
    _addLog("🌐 WebSocket 연결 시도");
    try {
      final uri = Uri.parse(_backendBase);
      final wsUrl = 'ws://${uri.host}:5000';
      print('尝试连接 WebSocket: $wsUrl');
      final channel = IOWebSocketChannel.connect(wsUrl);
      _wsChannel = channel;
      _wsSubscription = channel.stream.listen(
        (event) {
          _handleWsMessage(event);
        },
        onError: (error) {
          _addLog("❌ WebSocket 에러: $error");
          _cleanupWebSocket();
          _scheduleWsReconnect();
        },
        onDone: () {
          _addLog("💔 WebSocket 연결 종료");
          _cleanupWebSocket();
          _scheduleWsReconnect();
        },
      );
      _addLog("✅ WebSocket 연결 성공: $wsUrl");
    } catch (e) {
      _addLog("❌ WebSocket 연결 실패: $e");
      _cleanupWebSocket();
      _scheduleWsReconnect();
    }
  }

  void _cleanupWebSocket() {
    _addLog("🧹 WebSocket 정리");
    try {
      _wsSubscription?.cancel();
    } catch (_) {}
    _wsSubscription = null;
    try {
      _wsChannel?.sink.close();
    } catch (_) {}
    _wsChannel = null;
  }

  void _scheduleWsReconnect() {
    if (!mounted) return;
    _addLog("🔄 WebSocket 5초 후 재연결 시도 예약");
    Future.delayed(const Duration(seconds: 5), () {
      if (!mounted) return;
      if (_wsChannel == null) {
        _connectWebSocket();
      }
    });
  }

  Future<void> _handleWsMessage(dynamic event) async {
    if (_state != AppState.searching) return;
    if (_foundAnnounced) return;
    _addLog("📨 WebSocket 메시지 수신: $event");

    try {
      final raw = event.toString();

      dynamic data;
      try {
        data = json.decode(raw);
      } catch (_) {
        // If it's not JSON, fall back to raw text checks.
        data = raw;
      }

      final found = isFoundPayload(data) || isFoundPayload(raw);

      if (found && !_foundAnnounced && _state == AppState.searching) {
        _addLog("✅ 물품 발견! (WebSocket)");
        // ✅ 요구사항: "찾았습니다"를 말한 뒤 손 유도 모드로 전환
        await _announceFoundThenStartHandGuidance();
      }
    } catch (e) {
      print('解析 WebSocket 消息失败: $e');
    }
  }

  void _startContinuousPolling(int sessionId) {
    _pollTimer?.cancel();
    const pollInterval = Duration(seconds: 1);
    _foundAnnounced = false;

    _pollTimer = Timer.periodic(pollInterval, (timer) async {
      if (sessionId != _searchSessionId) {
        timer.cancel();
        return;
      }
      if (_state != AppState.searching) {
        timer.cancel();
        return;
      }
      if (_foundAnnounced) {
        timer.cancel();
        return;
      }

      try {
        final resp = await _dio.get(
          '$_backendBase/status',
          options: Options(
            sendTimeout: const Duration(seconds: 2),
            receiveTimeout: const Duration(seconds: 2),
          ),
        );
        final data = resp.data;

        final isFound = isFoundPayload(data);
        if (isFound && !_foundAnnounced && _state == AppState.searching) {
          _addLog("✅ 물품 발견! (Polling)");
          if (sessionId != _searchSessionId) return;
          timer.cancel();
          // ✅ 요구사항: "찾았습니다"를 말한 뒤 손 유도 모드로 전환
          await _announceFoundThenStartHandGuidance();
        }
      } catch (e) {}
    });

    Future.delayed(const Duration(seconds: 80), () async {
      if (!mounted) return;
      if (sessionId != _searchSessionId) return;
      if (mounted && _state == AppState.searching && !_foundAnnounced) {
        _pollTimer?.cancel();
        _abortGuidance?.complete();
        try {
          await _tts.stop();
        } catch (_) {}
        await _tts.speak('죄송합니다. 아직 찾지 못했습니다.');
        setState(() {
          _state = AppState.idle;
        });
      }
    });
  }

  Future<bool> _runOrAbort(Future<dynamic> futureToRun) async {
    if (_abortGuidance == null || _abortGuidance!.isCompleted) return false;
    await Future.any([futureToRun, _abortGuidance!.future]);
    if (_abortGuidance!.isCompleted) {
      try {
        await _tts.stop();
      } catch (_) {}
      return false;
    }
    return true;
  }

  Future<void> _startGuidanceTTSLoop() async {
    _abortGuidance ??= Completer<void>();
    const int maxLoops = 3;
    const Duration lookDuration = Duration(seconds: 5);

    if (_state != AppState.searching) return;

    for (int loop = 0; loop < maxLoops; loop++) {
      if (!await _runOrAbort(_tts.speak('고개를 약간 들어 정면 위를 바라보고 5초간 움직이지 마세요.')))
        return;
      if (!await _runOrAbort(Future.delayed(lookDuration))) return;

      if (!await _runOrAbort(_tts.speak('정면을 바라보고 5초간 움직이지 마세요'))) return;
      if (!await _runOrAbort(Future.delayed(lookDuration))) return;

      if (!await _runOrAbort(_tts.speak('고개를 약간 숙여 정면 아래를 바라보고 5초간 움직이지 마세요')))
        return;
      if (!await _runOrAbort(Future.delayed(lookDuration))) return;

      if (_abortGuidance!.isCompleted) return;
    }

    if (_state == AppState.searching) {
      _pollTimer?.cancel();
      await _tts.stop();
      await _tts.speak('죄송합니다. 아직 찾지 못했습니다. 다시 시도하려면 "다시 찾아줘"라고 말씀해 주세요');
      setState(() {
        _state = AppState.idle;
      });
    }
  }

  Future<void> _onSuccess() async {
    _addLog("🎉 성공! 물품을 찾았습니다");
    try {
      if (!(_abortGuidance?.isCompleted ?? true)) _abortGuidance?.complete();
    } catch (_) {}
    try {
      await _tts.stop();
    } catch (_) {}

    // [통합] 손 유도 관련 타이머도 정리
    _repeatTimer?.cancel();

    setState(() {
      _state = AppState.success;
    });
    await _tts.speak('물품을 찾았습니다.');
    await Future.delayed(const Duration(milliseconds: 800));
    setState(() {
      _state = AppState.idle;
    });
  }

  Color _backgroundColor() {
    switch (_state) {
      case AppState.idle:
        return Colors.blueAccent;
      case AppState.listening:
        return Colors.greenAccent;
      case AppState.searching:
        return Colors.amberAccent;
      case AppState.guiding:
        return Colors.purpleAccent; // [추가] 손 유도 색상
      case AppState.success:
        return Colors.white;
      case AppState.fail:
        return Colors.grey;
    }
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Voice Shopping Assistant',
      home: Scaffold(
        body: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: _onTap, // [1.dart 유지] idle일 때 탭하면 음성인식
          child: Stack(
            children: [
              // 1. [1.dart 유지] 배경색 애니메이션
              AnimatedContainer(
                duration: const Duration(milliseconds: 300),
                color: _backgroundColor(),
                width: double.infinity,
                height: double.infinity,
              ),

              // 2. [main.dart 기능] 정보 오버레이 (IP 주소, 상태)
              Positioned.fill(
                child: SafeArea(
                  child: Column(
                    children: [
                      const SizedBox(height: 20),
                      Text('HoloLens Guide Mode',
                          style: TextStyle(
                              fontSize: 20,
                              fontWeight: FontWeight.bold,
                              color: Colors.black54)),
                      Text('IP: $_myIpDisplay',
                          style:
                              TextStyle(fontSize: 16, color: Colors.black87)),
                      const Spacer(),

                      // 상태 표시 (디버깅용)
                      Text(
                        _state.toString().split('.').last.toUpperCase(),
                        style: const TextStyle(
                            fontSize: 30,
                            fontWeight: FontWeight.bold,
                            color: Colors.white30),
                      ),

                      // 안내 텍스트
                      Padding(
                        padding: const EdgeInsets.all(20.0),
                        child: Text(
                          _state == AppState.guiding
                              ? "손을 움직여 물건을 잡으세요\n($_currentInstruction)"
                              : _state == AppState.idle
                                  ? "화면을 터치하여 말하세요"
                                  : "...",
                          textAlign: TextAlign.center,
                          style: const TextStyle(
                              fontSize: 18, color: Colors.white70),
                        ),
                      ),

                      const SizedBox(height: 50),
                      // ★★★ [新增 2] 日志显示窗口 (Log Console) ★★★
                      // =======================================================
                      Expanded(
                        child: Container(
                          width: double.infinity,
                          color: Colors.black.withOpacity(0.7), // 半透明黑色背景
                          padding: const EdgeInsets.all(8.0),
                          child: ListView.builder(
                            controller: _logScrollController,
                            itemCount: _logs.length,
                            itemBuilder: (context, index) {
                              return Text(
                                _logs[index],
                                style: const TextStyle(
                                    color: Colors.greenAccent, fontSize: 12),
                              );
                            },
                          ),
                        ),
                      ),
                      // [main.dart] 시뮬레이션 버튼 (테스트용, 필요 없으면 삭제 가능)
                      // 터치 이벤트를 뺏지 않도록 Row/Column 배치 주의
                      /*
                      if (_state == AppState.searching || _state == AppState.guiding)
                        Container(
                          padding: const EdgeInsets.all(8),
                          color: Colors.black12,
                          child: Wrap(
                            spacing: 10,
                            children: [
                              ElevatedButton(onPressed: () => _handleTcpMessage('found'), child: const Text("Sim Found")),
                              ElevatedButton(onPressed: () => _handleTcpMessage('left(0.5)'), child: const Text("Sim Left")),
                              ElevatedButton(onPressed: () => _handleTcpMessage('grabbed'), child: const Text("Sim Grabbed")),
                            ],
                          ),
                        ),
                       */
                    ],
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
