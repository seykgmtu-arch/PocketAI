using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PocketAI.App.Services;
using PocketAI.Core.Configuration;
using PocketAI.Core.Models;
using PocketAI.Knowledge;

namespace PocketAI.App;

internal static class Milestone2SelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "PocketAI-M2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new KnowledgeStore(root);
            var files = new List<string>();
            foreach (var extension in new[] { "txt", "md", "csv", "json", "log", "cs" })
            {
                var path = Path.Combine(root, "sample." + extension);
                await File.WriteAllTextAsync(path, "Космодром Северный: старт экспедиции в 2042 году.", cancellationToken);
                files.Add(path);
            }
            var docx = Path.Combine(root, "sample.docx");
            using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
                writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Маяк Лазурный расположен на острове.</w:t></w:r></w:p></w:body></w:document>");
            }
            files.Add(docx);
            await store.ImportAsync(files, cancellationToken);
            Require(await store.GetDocumentCountAsync(cancellationToken) == 7, "Import all seven formats");
            Require((await store.SearchAsync("Лазурный", cancellationToken: cancellationToken)).Single().DocumentName == "sample.docx", "DOCX text retrieval");
            await store.ImportAsync([files[0]], cancellationToken);
            Require(await store.GetDocumentCountAsync(cancellationToken) == 7, "Reimport does not duplicate documents");
            var extra = Path.Combine(root, "extra.txt");
            await File.WriteAllTextAsync(extra, "Unique rollbackmarker content", cancellationToken);
            try { await store.ImportAsync([extra, Path.Combine(root, "missing.txt")], cancellationToken); throw new Exception("Expected failed import"); }
            catch (FileNotFoundException) { }
            Require(await store.GetDocumentCountAsync(cancellationToken) == 7, "Failed import leaves memory unchanged");
            Require(await new KnowledgeStore(root).GetDocumentCountAsync(cancellationToken) == 7, "Index survives reload");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                await store.SearchAsync("Северный", cancellationToken: cancellationToken);
                await store.ImportAsync([files[0]], cancellationToken);
            }));
            Require(await store.GetDocumentCountAsync(cancellationToken) == 7, "Concurrent reads/imports");

            const string secret = "PRIVATE_SENTINEL_key_document_prompt_937451";
            var modelDirectory = Path.Combine(root, "models", "chat", "nested");
            Directory.CreateDirectory(modelDirectory);
            var modelPath = Path.Combine(modelDirectory, secret + ".gguf");
            await File.WriteAllTextAsync(modelPath, secret, cancellationToken);
            var catalog = new LocalModelCatalog(root);
            var models = catalog.ScanChatModels();
            Require(models.Count == 1 && models[0].SizeBytes > 0, "Nested GGUF discovery");
            var config = new PocketAiConfig { ModelPath = models[0].RelativePath, SystemPrompt = secret };
            ConfigLoader.Save(root, config);
            Require(ConfigLoader.Load(root).ModelPath == config.ModelPath, "Selected model persists");
            Require(catalog.FindConfiguredModel(models, config.ModelPath.Replace('/', '\\')) is not null, "Windows path matching");
            using (var manager = new PocketAI.Inference.LlamaServerManager(root, config))
            using (var viewModel = new ViewModels.MainViewModel(config, new PocketAI.Hardware.HardwareProbe(), manager))
            {
                Require(!viewModel.ApplyModelCommand.CanExecute(null), "Model switching disabled during initialization");
                var window = new MainWindow(viewModel);
                Require(window.FindName("PromptBox") is not null && window.FindName("ChatList") is not null, "Milestone 2 XAML loads");
                window.Close();
            }
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            await File.WriteAllTextAsync(Path.Combine(root, "logs", secret + ".log"), "Authorization: Bearer " + secret, cancellationToken);
            var diagnostic = await new DiagnosticReportService(root).CreateAsync(config, null, secret, secret, secret, models, 7, cancellationToken);
            using (var archive = ZipFile.OpenRead(diagnostic))
            {
                Require(archive.GetEntry("hardware.json") is not null, "Diagnostic hardware metadata");
                Require(archive.GetEntry("logs.summary.json") is not null, "Diagnostic log summary");
                foreach (var entry in archive.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    var content = await reader.ReadToEndAsync(cancellationToken);
                    Require(!content.Contains(secret) && !entry.FullName.Contains(secret), "No secrets, prompts, model/document content or file names in diagnostic ZIP");
                    Require(!content.Contains(root), "No absolute user paths in diagnostics");
                }
            }
            await store.ClearAsync(cancellationToken);
            Require(await new KnowledgeStore(root).GetDocumentCountAsync(cancellationToken) == 0 && File.Exists(files[0]), "Clear index preserves source documents");
        }
        finally
        {
            // The directory was created with a unique name beneath the system temp folder above.
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("PocketAI-M2-tests-", StringComparison.Ordinal))
                Directory.Delete(full, recursive: true);
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Milestone 2 self-test failed: " + name);
    }
}
