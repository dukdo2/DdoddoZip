using System.Diagnostics;
using System.Text;
using DdoddoZip.Core;
using DdoddoZip.Cli;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return Commands.Run(args, cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("취소했습니다.");
    return ExitCodes.Cancelled;
}
catch (ArchiveException e)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"오류: {e.Message}");
    return ExitCodes.Fatal;
}
catch (UsageException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Commands.Usage);
    return ExitCodes.Usage;
}

namespace DdoddoZip.Cli
{
    internal static class ExitCodes
    {
        public const int Ok = 0;
        public const int Partial = 1;
        public const int Fatal = 2;
        public const int Cancelled = 3;
        public const int Usage = 4;
    }

    internal sealed class UsageException(string message) : Exception(message);

    internal static class Commands
    {
        public const string Usage = """
            또또집 명령줄 도구 (ddz)

            사용법:
              ddz l <압축파일> [--cp 949|65001]                    목록 보기
              ddz x <압축파일> [-o 풀위치] [옵션]                   압축 풀기
                  --folder auto|always|never   압축 파일 이름 폴더 만들기 (기본: auto)
                  --overwrite ask|overwrite|skip|rename           (기본: ask)
                  -p <암호>  --cp <코드페이지>  --base <압축파일 안 폴더>
                  --only <압축파일 안 경로>    이 경로(폴더면 그 안 전체)만 풀기, 여러 번 사용 가능
              ddz t <압축파일> [-p 암호]                            무결성 검사
              ddz a <새 압축파일> <파일/폴더...> [옵션]             압축하기
                  --format zip|7z|tar|tar.gz|tar.xz|tar.bz2       (기본: 확장자로 판단)
                  --level store|fast|normal|max                   (기본: normal)
                  -p <암호>  --encrypt-names (7z)  -y (있으면 덮어쓰기)
              ddz quickzip <파일/폴더...>                           "○○.zip으로 압축하기"
              ddz quickzip --list <목록파일>                        목록 파일(한 줄에 경로 하나)로 받기

            종료 코드: 0 성공, 1 일부 실패, 2 오류, 3 취소, 4 사용법 오류
            """;

        public static int Run(string[] args, CancellationToken ct)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help" or "/?")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok;
            }

            var a = new ArgReader(args[1..]);
            return args[0].ToLowerInvariant() switch
            {
                "l" or "list" => List(a, ct),
                "x" or "extract" => Extract(a, ct),
                "t" or "test" => Test(a, ct),
                "a" or "add" => Add(a, ct),
                "quickzip" => QuickZip(a, ct),
                _ => throw new UsageException($"알 수 없는 명령입니다: {args[0]}"),
            };
        }

        private static int List(ArgReader a, CancellationToken ct)
        {
            var cp = a.Int("--cp");
            var password = a.Value("-p");
            var path = a.SinglePositional("압축 파일");
            var console = new ConsoleInteraction();

            using var session = ArchiveSession.Open(path, new ArchiveOpenOptions { CodePage = cp, Password = password }, console, ct);
            Console.WriteLine($"형식: {session.FormatName}" +
                (session.NameEncoding is { } enc ? $"    파일명 인코딩: {enc.Display}" : "") +
                (session.HasEncryptedEntries ? "    암호: 있음" : ""));
            Console.WriteLine();
            Console.WriteLine($"{"수정한 날짜",-16}  {"크기",14}  {"압축 크기",14}  이름");
            Console.WriteLine(new string('-', 72));
            foreach (var e in session.Entries)
            {
                var date = e.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
                var size = e.IsDirectory ? "<폴더>" : e.Size.ToString("N0");
                var packed = e.IsDirectory ? "" : e.PackedSize.ToString("N0");
                Console.WriteLine($"{date,-16}  {size,14}  {packed,14}  {e.Path}{(e.IsEncrypted ? " *" : "")}");
            }
            Console.WriteLine(new string('-', 72));
            var files = session.Entries.Count(e => !e.IsDirectory);
            var folders = session.Entries.Count - files;
            Console.WriteLine($"파일 {files:N0}개, 폴더 {folders:N0}개, 전체 {Format.Bytes(session.TotalSize)}, 압축 파일 {Format.Bytes((ulong)new FileInfo(session.ArchivePath).Length)}");
            return ExitCodes.Ok;
        }

        private static int Extract(ArgReader a, CancellationToken ct)
        {
            var output = a.Value("-o");
            var folder = a.Choice("--folder", FolderMode.Auto);
            var overwrite = a.Choice("--overwrite", OverwriteMode.Ask);
            var password = a.Value("-p");
            var cp = a.Int("--cp");
            var baseFolder = a.Value("--base");
            var only = a.Values("--only");
            var path = a.SinglePositional("압축 파일");
            var console = new ConsoleInteraction();

            using var session = ArchiveSession.Open(path, new ArchiveOpenOptions { CodePage = cp, Password = password }, console, ct);

            IReadOnlyCollection<int>? indices = null;
            if (only.Count > 0)
            {
                var prefixes = only.Select(o => o.Replace('/', '\\').TrimEnd('\\')).ToList();
                indices = session.Entries
                    .Where(e => prefixes.Any(p => e.Path.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                                                  e.Path.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)))
                    .Select(e => e.Index).ToList();
                if (indices.Count == 0)
                    throw new UsageException("--only 에 해당하는 항목이 압축 파일 안에 없습니다.");
            }

            var options = new ExtractOptions
            {
                Destination = output ?? Path.GetDirectoryName(Path.GetFullPath(path))!,
                FolderMode = folder,
                Overwrite = overwrite,
                Password = password,
                BaseFolder = baseFolder,
                Indices = indices,
            };

            using var bar = new ConsoleProgress("푸는 중");
            var sw = Stopwatch.StartNew();
            var result = session.Extract(options, console, bar, ct);
            bar.Finish();

            Console.WriteLine($"풀었습니다: {result.OutputDirectory}");
            Console.WriteLine($"파일 {result.FilesWritten:N0}개 저장, {result.Skipped:N0}개 건너뜀 ({sw.Elapsed.TotalSeconds:N1}초)");
            return Report(result);
        }

        private static int Test(ArgReader a, CancellationToken ct)
        {
            var password = a.Value("-p");
            var path = a.SinglePositional("압축 파일");
            var console = new ConsoleInteraction();
            using var session = ArchiveSession.Open(path, new ArchiveOpenOptions { Password = password }, console, ct);
            using var bar = new ConsoleProgress("검사 중");
            var result = session.Test(console, bar, ct);
            bar.Finish();
            Console.WriteLine(result.HasProblems ? "문제가 있는 파일이 있습니다." : $"이상 없음: 파일 {result.FilesTested:N0}개");
            return Report(result);
        }

        private static int Add(ArgReader a, CancellationToken ct)
        {
            var formatText = a.Value("--format");
            var level = a.Choice("--level", CompressLevel.Normal, new()
            {
                ["store"] = CompressLevel.Store, ["fast"] = CompressLevel.Fast,
                ["normal"] = CompressLevel.Normal, ["max"] = CompressLevel.Maximum,
            });
            var password = a.Value("-p");
            var encryptNames = a.Flag("--encrypt-names");
            var force = a.Flag("-y");
            var positionals = a.Positionals();
            if (positionals.Count < 2)
                throw new UsageException("새 압축 파일 이름과 압축할 파일을 적어 주세요.");

            var dest = positionals[0];
            ArchiveKind kind;
            if (formatText is not null)
            {
                if (!ArchiveKindInfo.TryParse(formatText, out kind))
                    throw new UsageException($"지원하지 않는 형식입니다: {formatText}");
            }
            else if (!TryKindFromName(dest, out kind))
            {
                throw new UsageException("확장자로 형식을 알 수 없습니다. --format 을 지정해 주세요.");
            }

            if (File.Exists(dest) && !force)
                throw new UsageException($"이미 있는 파일입니다: {dest} (덮어쓰려면 -y)");

            using var bar = new ConsoleProgress("압축 중");
            var sw = Stopwatch.StartNew();
            var result = Compressor.Compress(positionals.Skip(1).ToList(), dest,
                new CompressOptions { Kind = kind, Level = level, Password = password, EncryptFileNames = encryptNames }, bar, ct);
            bar.Finish();
            PrintCompressResult(result, sw.Elapsed);
            return ExitCodes.Ok;
        }

        private static int QuickZip(ArgReader a, CancellationToken ct)
        {
            var listFile = a.Value("--list");
            var sources = new List<string>(a.Positionals());
            if (listFile is not null)
            {
                sources.AddRange(File.ReadAllLines(listFile, Encoding.UTF8).Where(l => !string.IsNullOrWhiteSpace(l)));
            }
            if (sources.Count == 0)
                throw new UsageException("압축할 파일이나 폴더를 적어 주세요.");

            using var bar = new ConsoleProgress("압축 중");
            var sw = Stopwatch.StartNew();
            var result = Compressor.QuickZip(sources, bar, ct);
            bar.Finish();
            PrintCompressResult(result, sw.Elapsed);
            return ExitCodes.Ok;
        }

        private static void PrintCompressResult(CompressResult r, TimeSpan elapsed)
        {
            var ratio = r.SourceBytes == 0 ? 100 : 100.0 * r.ArchiveBytes / r.SourceBytes;
            Console.WriteLine($"만들었습니다: {r.OutputPath}");
            Console.WriteLine($"파일 {r.FileCount:N0}개, {Format.Bytes(r.SourceBytes)} → {Format.Bytes((ulong)r.ArchiveBytes)} ({ratio:N0}%), {elapsed.TotalSeconds:N1}초");
        }

        private static int Report(ExtractResult result)
        {
            foreach (var blocked in result.BlockedPaths)
                Console.WriteLine($"  [차단] {blocked} (풀 위치 밖으로 나가는 위험한 경로)");
            foreach (var f in result.Failures)
                Console.WriteLine($"  [실패] {f.Path}: {f.Message}");
            return result.HasProblems ? ExitCodes.Partial : ExitCodes.Ok;
        }

        private static bool TryKindFromName(string path, out ArchiveKind kind)
        {
            var name = Path.GetFileName(path).ToLowerInvariant();
            foreach (var candidate in Enum.GetValues<ArchiveKind>().OrderByDescending(k => ArchiveKindInfo.Extension(k).Length))
            {
                if (name.EndsWith(ArchiveKindInfo.Extension(candidate)))
                {
                    kind = candidate;
                    return true;
                }
            }
            return ArchiveKindInfo.TryParse(Path.GetExtension(name), out kind);
        }
    }

    internal static class Format
    {
        public static string Bytes(ulong b) => b switch
        {
            < 1024 => $"{b} B",
            < 1024 * 1024 => $"{b / 1024.0:N1} KB",
            < 1024UL * 1024 * 1024 => $"{b / 1024.0 / 1024:N1} MB",
            _ => $"{b / 1024.0 / 1024 / 1024:N2} GB",
        };
    }

    /// <summary>Very small argument parser: "-x value", "--flag", positionals.</summary>
    internal sealed class ArgReader(string[] args)
    {
        private readonly List<string> _rest = [.. args];

        public string? Value(string name)
        {
            var i = _rest.IndexOf(name);
            if (i < 0) return null;
            if (i + 1 >= _rest.Count) throw new UsageException($"{name} 뒤에 값이 필요합니다.");
            var v = _rest[i + 1];
            _rest.RemoveRange(i, 2);
            return v;
        }

        public List<string> Values(string name)
        {
            var list = new List<string>();
            while (Value(name) is { } v) list.Add(v);
            return list;
        }

        public int? Int(string name) =>
            Value(name) is { } v ? (int.TryParse(v, out var n) ? n : throw new UsageException($"{name} 값은 숫자여야 합니다: {v}")) : null;

        public bool Flag(string name) => _rest.Remove(name);

        public T Choice<T>(string name, T fallback, Dictionary<string, T>? map = null) where T : struct, Enum
        {
            var v = Value(name);
            if (v is null) return fallback;
            if (map is not null && map.TryGetValue(v.ToLowerInvariant(), out var mapped)) return mapped;
            if (map is null && Enum.TryParse<T>(v, ignoreCase: true, out var parsed)) return parsed;
            throw new UsageException($"{name} 값이 올바르지 않습니다: {v}");
        }

        public List<string> Positionals()
        {
            var unknown = _rest.FirstOrDefault(r => r.StartsWith('-') && r.Length > 1 && !File.Exists(r) && !Directory.Exists(r));
            if (unknown is not null) throw new UsageException($"알 수 없는 옵션입니다: {unknown}");
            return [.. _rest];
        }

        public string SinglePositional(string what)
        {
            var p = Positionals();
            if (p.Count != 1) throw new UsageException($"{what}을(를) 하나 적어 주세요.");
            return p[0];
        }
    }
}
