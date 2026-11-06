{{flutter_js}}
{{flutter_build_config}}
for (const build of _flutter.buildConfig.builds) {
  if (build.mainJsPath) build.mainJsPath += '?v=1.1.25';
}
_flutter.loader.load({config: {canvasKitBaseUrl: 'canvaskit/'}});
