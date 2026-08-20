# Read the original file
$content = [System.IO.File]::ReadAllText("d:\Low Vision\voice_app\voice_app_new\lib\main.dart", [System.Text.Encoding]::UTF8)

# The methods to insert
$methodsToAdd = @"
  /// When user touches the screen, start voice input
  Future<void> _promptForInput() async {
    setState(() {
      _state = AppState.listening;
    });

    await _tts.speak('무엇을 찾고 계신가요?');
    
    try {
      final tempDir = await getTemporaryDirectory();
      _currentRecordingPath = '`${tempDir.path}/voice_`${DateTime.now().millisecondsSinceEpoch}.m4a';
      
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
      print('Recording error: `$e');
      setState(() {
        _state = AppState.idle;
      });
      await _tts.speak('오류가 발생했습니다. 다시 시도해주세요.');
    }
  }

  /// Upload audio file and recognize
  Future<void> _uploadAndRecognize(String audioPath) async {
    try {
      final formData = FormData.fromMap({
        'file': await MultipartFile.fromFile(audioPath, filename: 'voice.m4a'),
      });

      final response = await _dio.post(
        '`$_pythonApiBase/recognize',
        data: formData,
      );

      if (response.statusCode == 200 && response.data['text'] != null) {
        String recognizedText = response.data['text'];
        print('Recognition result: `$recognizedText');
        await _sendToBackend(recognizedText);
      } else {
        setState(() {
          _state = AppState.idle;
        });
        await _tts.speak('음성 인식 실패. 다시 시도해주세요.');
      }
    } catch (e) {
      print('Recognition error: `$e');
      setState(() {
        _state = AppState.idle;
      });
      await _tts.speak('오류가 발생했습니다.');
    }
  }

"@

# Insert before _sendToBackend method
$searchText = "  Future<void> _sendToBackend(String phrase) async {"
$replaceText = $methodsToAdd + $searchText

$newContent = $content.Replace($searchText, $replaceText)

# Write back
[System.IO.File]::WriteAllText("d:\Low Vision\voice_app\voice_app_new\lib\main.dart", $newContent, [System.Text.Encoding]::UTF8)

Write-Host "Done! Added _promptForInput() and _uploadAndRecognize() methods."

