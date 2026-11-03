using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityWaxKit.Editor
{
    /// <summary>
    /// WebGL build step: writes the server files that tell browsers how the build files are compressed (Netlify
    /// <c>_headers</c>, Apache <c>.htaccess</c>) next to index.html, on every build, so a compressed build loads once
    /// uploaded. Turn each file off in Project Settings > Unity WAX Kit.
    /// </summary>
    internal sealed class WaxKitWebGLBuild : IPostprocessBuildWithReport
    {
        // Same content as the README examples ("Building and publishing").
        const string NetlifyGzip = @"/Build/*.gz
  Content-Encoding: gzip
/Build/*.wasm.gz
  Content-Type: application/wasm
/Build/*.js.gz
  Content-Type: application/javascript
/Build/*.data.gz
  Content-Type: application/octet-stream
";

        const string NetlifyBrotli = @"/Build/*.br
  Content-Encoding: br
/Build/*.wasm.br
  Content-Type: application/wasm
/Build/*.js.br
  Content-Type: application/javascript
/Build/*.data.br
  Content-Type: application/octet-stream
";

        const string Apache = @"<IfModule mod_mime.c>
  RemoveType .gz .br
  AddEncoding gzip .gz
  AddEncoding br .br
  AddType application/wasm .wasm.gz .wasm.br
  AddType application/javascript .js.gz .js.br
  AddType application/octet-stream .data.gz .data.br .symbols.json.gz .symbols.json.br
</IfModule>
";

        /// <summary>Order among post-build steps (any order works).</summary>
        public int callbackOrder => 0;

        /// <summary>Writes the files after a WebGL build, when the build's compression needs them.</summary>
        public void OnPostprocessBuild(BuildReport report)
        {
            var summary = report.summary;
            if (summary.platform != BuildTarget.WebGL) return;
            if (summary.result == BuildResult.Failed || summary.result == BuildResult.Cancelled) return;

            var settings = WaxKitSettings.Load();
            if (!settings.webGLWriteNetlifyHeaders && !settings.webGLWriteApacheHtaccess) return;

            var compression = PlayerSettings.WebGL.compressionFormat;
            if (compression == WebGLCompressionFormat.Disabled)
            {
                Debug.Log("Unity WAX Kit: no server header files written: compression is Disabled, so the server needs no Content-Encoding headers.");
                return;
            }
            if (PlayerSettings.WebGL.decompressionFallback)
            {
                Debug.Log("Unity WAX Kit: no server header files written: Decompression Fallback is on, so the browser unpacks the files itself.");
                return;
            }

            var folder = summary.outputPath;
            if (!File.Exists(Path.Combine(folder, "index.html")))
            {
                Debug.LogWarning($"Unity WAX Kit: index.html was not found in {folder}; no server header files written.");
                return;
            }

            var written = new List<string>();
            if (settings.webGLWriteNetlifyHeaders)
            {
                File.WriteAllText(Path.Combine(folder, "_headers"), compression == WebGLCompressionFormat.Brotli ? NetlifyBrotli : NetlifyGzip);
                written.Add("_headers (Netlify)");
            }
            if (settings.webGLWriteApacheHtaccess)
            {
                File.WriteAllText(Path.Combine(folder, ".htaccess"), Apache);
                written.Add(".htaccess (Apache)");
            }
            Debug.Log($"Unity WAX Kit: wrote {string.Join(" and ", written)} for {compression} compression to {folder}.");
        }
    }
}
