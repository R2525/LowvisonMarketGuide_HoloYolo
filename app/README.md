# voice_app_new

A new Flutter project.

## Running

The app calls the OpenAI Whisper API directly and needs a key injected at build/run time
(it is intentionally not hardcoded in the source):

```
flutter run --dart-define=OPENAI_API_KEY=sk-...
```

## Backend server

The Python backend (`main.py`) lives in the `LowvisonMarketGuide_HoloYolo` repo, not here.
A stale copy under `python后端/` used to live in this repo and has been removed to avoid
the two drifting apart again.

## Getting Started

This project is a starting point for a Flutter application.

A few resources to get you started if this is your first Flutter project:

- [Lab: Write your first Flutter app](https://docs.flutter.dev/get-started/codelab)
- [Cookbook: Useful Flutter samples](https://docs.flutter.dev/cookbook)

For help getting started with Flutter development, view the
[online documentation](https://docs.flutter.dev/), which offers tutorials,
samples, guidance on mobile development, and a full API reference.
