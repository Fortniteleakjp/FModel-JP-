using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using FModel.Settings;
using Newtonsoft.Json;
using Serilog;

namespace FModel.Services.Mcp;

/// <summary>
/// バックグラウンドのエクスポートジョブ。ジョブごとに <c>Output/MCP/export-*</c> を新規に掘り、
/// raw / properties / audio / converted の各モードで書き出して最後に manifest.json を残す。
/// FModel の画面側のエクスポートセッション (ExportSessionViewModel) とは別の ExportSession を使うので、
/// ユーザーの手作業のキューと混ざらない。
/// </summary>
public sealed partial class FModelMcpService
{
    public async Task<object> StartExport(string sessionId, ExportRequest request, CancellationToken ct)
    {
        ValidateExport(request);
        var session = Session(sessionId);
        await InSession(session.Id, s =>
        {
            foreach (var path in request.Paths)
            {
                var f = File(s, path);
                CheckSize(f);
                McpPathPolicy.ValidateRelative(f.Path);
            }

            return true;
        }, ct);

        lock (_jobsSync)
        {
            if (session.Closed) throw new InvalidOperationException("Session is closed.");
            if (_jobs.Count >= MaxJobs)
            {
                var oldest = _jobs.Values.Where(j => IsTerminal(j) && j.Task?.IsCompleted == true).OrderBy(j => j.CreatedAt).FirstOrDefault()
                             ?? throw new InvalidOperationException("Job limit reached. Wait for an existing export to finish.");
                _jobs.TryRemove(oldest.Id, out _);
                oldest.Cancellation.Dispose();
            }

            var job = new McpExportJob(Guid.NewGuid().ToString("N")[..12], session.Id, McpPathPolicy.NewOutputDirectory("export"), request.Paths.Length);
            _jobs[job.Id] = job;
            job.Task = Task.Run(() => RunExport(job, session, request));
            return job.Snapshot();
        }
    }

    public object ListJobs() => new { jobs = _jobs.Values.OrderByDescending(j => j.CreatedAt).Select(j => j.Snapshot()).ToArray() };

    public object GetJob(string id) => Job(id).Snapshot();

    public object JobResults(string id, int offset, int limit)
    {
        var job = Job(id);
        lock (job.Sync) return new { status = job.Snapshot(), results = Paginate(job.Results.ToArray(), offset, limit) };
    }

    public object CancelJob(string id)
    {
        lock (_jobsSync)
        {
            var job = Job(id);
            lock (job.Sync)
            {
                if (!job.Terminal)
                {
                    job.State = "cancelling";
                    job.Cancellation.Cancel();
                }

                return job.Snapshot();
            }
        }
    }

    public object ReadOutput(string jobId, string relativePath, string encoding, int offset, int count)
    {
        var job = Job(jobId);
        if (!IsTerminal(job)) throw new InvalidOperationException("Wait for terminal job status before reading its output.");
        McpPathPolicy.ValidateRelative(relativePath);
        var path = Path.GetFullPath(Path.Combine(job.OutputDirectory, relativePath));
        if (!McpPathPolicy.IsWithin(job.OutputDirectory, path)) throw new ArgumentException("Output path escapes job directory.");
        McpPathPolicy.RejectLinksBelow(job.OutputDirectory, path);
        if (offset < 0 || count is < 1 or > 32768) throw new ArgumentException("offset must be nonnegative; count must be 1..32768 bytes.");
        if (encoding is not ("utf8" or "base64")) throw new ArgumentException("encoding must be utf8 or base64.");

        using var stream = System.IO.File.OpenRead(path);
        if (offset > stream.Length) throw new ArgumentException("offset is beyond file length.");
        stream.Position = offset;
        var bytes = new byte[(int) Math.Min(count, stream.Length - offset)];
        stream.ReadExactly(bytes);
        return new
        {
            path, offset, totalBytes = stream.Length, bytesRead = bytes.Length, encoding,
            nextOffset = offset + (long) bytes.Length < stream.Length ? (int?) (offset + bytes.Length) : null,
            text = encoding == "base64" ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes)
        };
    }

    private async Task RunExport(McpExportJob job, McpSession session, ExportRequest request)
    {
        var ct = job.Cancellation.Token;
        var finalState = "failed";
        try
        {
            lock (job.Sync)
            {
                ct.ThrowIfCancellationRequested();
                job.State = "running";
            }

            foreach (var path in request.Paths)
            {
                ct.ThrowIfCancellationRequested();
                lock (job.Sync) job.CurrentPath = path;

                McpExportItem result;
                // セッションのロックはアセット 1 件ごとに取る。長いジョブの間も他のツール (閲覧系) が割り込める
                await session.Gate.WaitAsync(ct);
                try
                {
                    if (session.Closed) throw new InvalidOperationException("Session is closed.");
                    result = await ExportOne(session, request, path, job.OutputDirectory, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = new McpExportItem(path, false, [], ErrorMessage(ex));
                }
                finally
                {
                    session.Gate.Release();
                }

                lock (job.Sync) job.Results.Add(result);
            }

            lock (job.Sync)
            {
                ct.ThrowIfCancellationRequested();
                finalState = job.Results.All(r => r.Success) ? "completed" : "completed_with_errors";
            }
        }
        catch (OperationCanceledException)
        {
            finalState = "cancelled";
        }
        catch (Exception ex)
        {
            lock (job.Sync)
            {
                finalState = "failed";
                job.Error = ErrorMessage(ex);
            }
        }
        finally
        {
            lock (job.Sync)
            {
                job.CurrentPath = null;
                job.State = "finalizing";
            }

            try
            {
                // キャンセル・失敗時に書けた分も消さずに残し、manifest に一覧する
                var manifest = McpPathPolicy.OutputFile(job.OutputDirectory, "manifest.json");
                await System.IO.File.WriteAllTextAsync(manifest, JsonConvert.SerializeObject(new
                {
                    status = job.Snapshot(finalState),
                    results = job.Results,
                    files = Directory.EnumerateFiles(job.OutputDirectory, "*", SearchOption.AllDirectories)
                        .Where(p => p != manifest)
                        .Select(p => new { path = Path.GetRelativePath(job.OutputDirectory, p).Replace('\\', '/'), bytes = new FileInfo(p).Length }).ToArray(),
                    note = "Cancellation and failures can leave partial files. Inspect per-asset success before using the outputs."
                }, Formatting.Indented));
            }
            catch (Exception ex)
            {
                lock (job.Sync)
                {
                    job.Error = "Manifest could not be written: " + ErrorMessage(ex);
                    finalState = "failed";
                }
            }

            lock (job.Sync) job.State = finalState;
            Log.Information("MCP export job {JobId} finished: {State} ({Count} assets) -> {Directory}", job.Id, finalState, job.Total, job.OutputDirectory);
        }
    }

    private async Task<McpExportItem> ExportOne(McpSession session, ExportRequest request, string path, string root, CancellationToken ct)
    {
        var file = File(session, path);
        CheckSize(file);
        McpPathPolicy.ValidateRelative(file.Path);

        if (request.Mode == "raw")
        {
            var data = file.IsUePackage ? session.Provider.SavePackage(file) : new Dictionary<string, byte[]> { [file.Path] = file.Read() };
            var written = new List<string>();
            foreach (var (name, bytes) in data)
            {
                ct.ThrowIfCancellationRequested();
                var output = McpPathPolicy.OutputFile(root, "raw/" + name);
                await System.IO.File.WriteAllBytesAsync(output, bytes, ct);
                written.Add(output);
            }

            return new McpExportItem(path, written.Count > 0, written.ToArray(), written.Count == 0 ? "Parser returned no raw files." : null);
        }

        var package = LoadPackage(session, file);
        var objects = request.ObjectName is null ? package.GetExports() : [package.GetExport(request.ObjectName)];

        if (request.Mode == "properties")
        {
            // FModel の「プロパティを保存」と同じ JSON (エクスポート配列)
            var output = McpPathPolicy.OutputFile(root, "properties/" + file.PathWithoutExtension + ".json");
            await using var stream = new StreamWriter(output, false, new UTF8Encoding(false));
            using var writer = new JsonTextWriter(stream) { Formatting = Formatting.Indented };
            var serializer = JsonSerializer.CreateDefault();
            writer.WriteStartArray();
            foreach (var obj in objects)
            {
                ct.ThrowIfCancellationRequested();
                serializer.Serialize(writer, obj);
            }

            writer.WriteEndArray();
            return new McpExportItem(path, true, [output]);
        }

        if (request.Mode == "audio")
        {
            var written = new List<string>();
            foreach (var obj in objects)
            {
                ct.ThrowIfCancellationRequested();
                obj.Decode(request.DecompressAudio, out var format, out var bytes);
                if (bytes is not { Length: > 0 }) continue;
                var output = McpPathPolicy.OutputFile(root, "audio/" + file.PathWithoutExtension + "/" + obj.Name + "." + format.ToLowerInvariant());
                await System.IO.File.WriteAllBytesAsync(output, bytes, ct);
                written.Add(output);
            }

            if (written.Count == 0)
                throw new NotSupportedException("No decodable SoundWave, SoundNodeWave or AkMediaAssetData export. Sound cues and external audio banks may reference separate audio assets; inspect their properties.");
            return new McpExportItem(path, true, written.ToArray());
        }

        var conversionRoot = Path.Combine(root, "converted");
        Directory.CreateDirectory(conversionRoot);
        var exports = new ExportSession((args, _) =>
        {
            // 画面版はサブレベル選択ダイアログを出すが、MCP では includeStreamingLevels で決める
            foreach (var level in args.StreamingLevels) level.IsPersistent = request.IncludeStreamingLevels;
            foreach (var actor in args.Actors) ConfigureSublevels(actor, request.IncludeStreamingLevels);
        })
        {
            MaxDegreeOfParallelism = 1
        };

        var skipped = new HashSet<string>();
        foreach (var obj in objects)
        {
            ct.ThrowIfCancellationRequested();
            if (obj is UAnimSequence { CompressedDataStructure: null, RawAnimationData: null or { Length: 0 } })
                throw new NotSupportedException("Animation compression data is unavailable. Mount its compression-settings/codec dependencies and use the matching game profile. Raw and properties exports remain available.");
            try
            {
                exports.Add(obj);
            }
            catch (NotSupportedException)
            {
                skipped.Add(obj.ExportType);
            }
        }

        if (!exports.HasQueuedItems)
            throw new NotSupportedException("No supported converted exports. Use properties/raw/audio mode for this asset. Types: " + string.Join(", ", skipped));

        var result = await exports.RunAsync(conversionRoot, BuildExportOptions(request, session), ct: ct);
        var errors = result.Where(r => !r.Success).Select(r => r.ObjectPath + ": " + ErrorMessage(r.Error ?? new Exception("Export failed"))).ToList();
        var outputs = result.SelectMany(r => r.DiskFilePaths ?? []).ToArray();

        // CUE4Parse 本体は書き出し先を検証しない (JP 版はコアを改変しない方針) ので、書いた後に確かめる
        var escaped = outputs.Where(o => !McpPathPolicy.IsWithin(conversionRoot, o)).ToArray();
        if (escaped.Length > 0)
            errors.Add("Some dependency outputs were written outside the job directory: " + string.Join(", ", escaped));

        return new McpExportItem(path, result.Count > 0 && errors.Count == 0, outputs,
            errors.Count > 0 ? string.Join("; ", errors) : skipped.Count > 0 ? "Skipped unsupported export types: " + string.Join(", ", skipped) : null);
    }

    private static void ValidateExport(ExportRequest r)
    {
        if (r.Paths is not { Length: > 0 } || r.Paths.Length > MaxBatchAssets || r.Paths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Supply 1..{MaxBatchAssets} explicit asset paths.");
        if (r.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != r.Paths.Length)
            throw new ArgumentException("Duplicate export paths are not allowed.");
        r.Mode = (r.Mode ?? "converted").ToLowerInvariant();
        if (r.Mode is not ("raw" or "properties" or "converted" or "audio"))
            throw new ArgumentException("mode must be raw, properties, converted or audio.");
        if (r.ObjectName is not null && r.Paths.Length != 1) throw new ArgumentException("objectName is only supported for a single package.");
        if (r.Mode == "raw" && r.ObjectName is not null) throw new ArgumentException("Raw export works at package/file level; omit objectName.");
        if (r.TextureQuality is < 1 or > 100) throw new ArgumentException("textureQuality must be 1..100.");
        if (r.MeshFormat is not null) ParseEnum<EMeshFormat>(r.MeshFormat);
        if (r.MeshQuality is not null) ParseEnum<EMeshQuality>(r.MeshQuality);
        if (r.NaniteMeshFormat is not null) ParseEnum<ENaniteMeshFormat>(r.NaniteMeshFormat);
        if (r.TextureFormat is not null) ParseEnum<ETextureFormat>(r.TextureFormat);
        if (r.MaterialDepth is not null) ParseEnum<EMaterialDepth>(r.MaterialDepth);
        if (r.SocketFormat is not null) ParseEnum<ESocketFormat>(r.SocketFormat);
        if (r.CompressionFormat is not null) ParseEnum<EFileCompressionFormat>(r.CompressionFormat);
    }

    /// <summary>リクエストで指定が無い項目は FModel の設定画面 (エクスポート) の値を使う</summary>
    private static ExportOptions BuildExportOptions(ExportRequest r, McpSession s)
    {
        var d = UserSettings.Default;
        return new ExportOptions(
            meshFormat: r.MeshFormat is null ? d.MeshExportFormat : ParseEnum<EMeshFormat>(r.MeshFormat),
            naniteMeshFormat: r.NaniteMeshFormat is null ? d.NaniteMeshExportFormat : ParseEnum<ENaniteMeshFormat>(r.NaniteMeshFormat),
            meshQuality: r.MeshQuality is null ? d.MeshQuality : ParseEnum<EMeshQuality>(r.MeshQuality),
            texturePlatform: s.Provider.Versions.Platform,
            textureFormat: r.TextureFormat is null ? d.TextureExportFormat : ParseEnum<ETextureFormat>(r.TextureFormat),
            textureQuality: r.TextureQuality ?? d.TextureQuality,
            exportHdrTexturesAsHdr: r.ExportHdrTexturesAsHdr ?? d.SaveHdrTexturesAsHdr,
            exportAllTextureMips: r.ExportAllTextureMips ?? d.ExportAllTextureMips,
            materialDepth: r.MaterialDepth is null ? d.MaterialExportFormat : ParseEnum<EMaterialDepth>(r.MaterialDepth),
            exportMaterials: r.ExportMaterials ?? d.SaveEmbeddedMaterials,
            exportMorphTargets: r.ExportMorphTargets ?? d.SaveMorphTargets,
            socketFormat: r.SocketFormat is null ? d.SocketExportFormat : ParseEnum<ESocketFormat>(r.SocketFormat),
            compressionFormat: r.CompressionFormat is null ? d.CompressionFormat : ParseEnum<EFileCompressionFormat>(r.CompressionFormat));
    }

    private McpExportJob Job(string id) =>
        _jobs.TryGetValue(id ?? string.Empty, out var job) ? job : throw new KeyNotFoundException("Unknown or expired jobId. Use fmodel_list_jobs.");

    private static bool IsTerminal(McpExportJob job)
    {
        lock (job.Sync) return job.Terminal;
    }

    private static void ConfigureSublevels(ActorDto actor, bool include)
    {
        foreach (var level in actor.StreamingLevels ?? []) level.IsPersistent = include;
        ConfigureComponentSublevels(actor.RootComponent, include);
    }

    private static void ConfigureComponentSublevels(SceneComponentDto component, bool include)
    {
        if (component is null) return;
        foreach (var child in component.Children) ConfigureComponentSublevels(child, include);
        foreach (var actor in component.AttachedActors) ConfigureSublevels(actor, include);
    }
}
