// Editor-only helper: builds the standalone shader AssetBundle for the key-background blur.
// Run headless from tools\Build-BlurBundle.ps1:
//   Unity.exe -batchmode -quit -nographics -projectPath <this project> -executeMethod JkvBundleBuilder.Build
// 仅编辑器辅助：构建按键背景模糊的独立 shader AssetBundle。由 tools\Build-BlurBundle.ps1 无头调用。
using System.IO;
using UnityEditor;

public static class JkvBundleBuilder
{
    // Unity writes relative to the project folder. NOT under Temp/: Unity deletes the whole
    // <project>/Temp folder on quit, so a bundle written there vanishes before the caller can
    // copy it (the first build "succeeded" and left nothing behind). /
    // 相对工程根目录写出。**不能**放在 Temp/ 下：Unity 退出时会删除整个 <project>/Temp 目录，
    // 写在那里的 bundle 在调用方拷贝之前就消失了（首次构建「成功」却没留下任何文件）。
    private const string OutputDir = "jkv-bundle-out";
    private const string BundleName = "keyviewerblur.bundle";
    private const string ShaderAsset = "Assets/Shaders/KeyViewerBlur.shader";

    public static void Build()
    {
        Directory.CreateDirectory(OutputDir);
        AssetBundleBuild[] builds =
        {
            new AssetBundleBuild
            {
                assetBundleName = BundleName,
                assetNames = new[] { ShaderAsset },
            },
        };
        // ChunkBasedCompression = LZ4: loads without full decompression, tiny file for one shader.
        // ChunkBasedCompression = LZ4：无需整体解压即可加载，单个 shader 体积也小。
        BuildPipeline.BuildAssetBundles(OutputDir, builds, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        string final = Path.Combine(OutputDir, BundleName);
        if (!File.Exists(final))
            throw new IOException($"bundle missing after build: {final}");
        EditorApplication.Exit(0);
    }
}
