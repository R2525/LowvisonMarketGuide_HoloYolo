import sys

# Read the file
with open(r'd:\Low Vision\voice_app\voice_app_new\lib\main.dart', 'r', encoding='utf-8') as f:
    lines = f.readlines()

# The methods to insert
methods_to_add = '''
  // ====================================================
  // 语音输入处理
  // ====================================================

  /// 当用户触摸屏幕时，启动语音输入
  Future<void> _promptForInput() async {
    setState(() {
      _state = AppState.listening;
    });

    await _tts.speak('무엇을 찾고 계신가요?');
    
    try {
      final tempDir = await getTemporaryDirectory();
      _currentRecordingPath = '${tempDir.path}/voice_${DateTime.now().millisecondsSinceEpoch}.m4a';
      
      if (await _record.hasPermission()) {
        await _record.start(
          const RecordConfig(encoder: AudioEncoder.aacLc),
          path: _currentRecordingPath,
        );

        await Future.delayed(const Duration(seconds: 5));
        
        final path = await _record.stop();
        if (path != null && path.isNotEmpty) {
          await _uploadAndRecognize(path);
        } else {
          setState(() {
            _state = AppState.idle;
          });
          await _tts.speak('녹음 실패. 다시 시도해주세요.');
        }
      } else {
        setState(() {
          _state = AppState.idle;
        });
        await _tts.speak('마이크 권한이 필요합니다.');
      }
    } catch (e) {
      print('Recording error: $e');
      setState(() {
        _state = AppState.idle;
      });
      await _tts.speak('오류가 발생했습니다. 다시 시도해주세요.');
    }
  }

  /// 上传音频文件并识别
  Future<void> _uploadAndRecognize(String audioPath) async {
    try {
      final formData = FormData.fromMap({
        'file': await MultipartFile.fromFile(audioPath, filename: 'voice.m4a'),
      });

      final response = await _dio.post(
        '$_pythonApiBase/recognize',
        data: formData,
      );

      if (response.statusCode == 200 && response.data['text'] != null) {
        String recognizedText = response.data['text'];
        print('Recognition result: $recognizedText');
        await _sendToBackend(recognizedText);
      } else {
        setState(() {
          _state = AppState.idle;
        });
        await _tts.speak('음성 인식 실패. 다시 시도해주세요.');
      }
    } catch (e) {
      print('Recognition error: $e');
      setState(() {
        _state = AppState.idle;
      });
      await _tts.speak('오류가 발생했습니다.');
    }
  }

'''

# Insert after line 316 (index 315, since 0-indexed)
# Line 316 is the blank line after "  }"
new_lines = lines[:316] + [methods_to_add] + lines[316:]

# Write back
with open(r'd:\Low Vision\voice_app\voice_app_new\lib\main.dart', 'w', encoding='utf-8') as f:
    f.writelines(new_lines)

print("Successfully added _promptForInput() and _uploadAndRecognize() methods!")
