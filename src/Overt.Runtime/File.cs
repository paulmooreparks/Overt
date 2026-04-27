// Filesystem operations: File (read/write/exists/etc.), Directory
// (list/create/delete), and pure path-string helpers under Path. All
// fallible operations surface host exceptions as Result<T, IoError>.

namespace Overt.Runtime;

/// <summary>
/// File I/O companion. Mirrors the small set of file operations that Overt
/// programs need without pulling in an extern binding per call. All
/// fallible operations return <c>Result&lt;T, IoError&gt;</c>; the
/// host-side exceptions are converted to <c>IoError</c> at the boundary
/// per DESIGN.md §17. Pure path-string helpers live on <see cref="Path"/>.
/// </summary>
public static class File
{
    /// <summary>Read the file at <paramref name="path"/> as UTF-8 and
    /// return its contents. Errors (file not found, permission denied,
    /// encoding failure) surface as <c>Err(IoError)</c>.</summary>
    public static Result<string, IoError> read_to_string(string path)
    {
        try
        {
            return new ResultOk<string, IoError>(global::System.IO.File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return new ResultErr<string, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Write <paramref name="contents"/> to <paramref name="path"/>
    /// as UTF-8, overwriting any existing file. Returns
    /// <c>Ok(())</c> on success, <c>Err(IoError)</c> on failure.</summary>
    public static Result<Unit, IoError> write_all_text(string path, string contents)
    {
        try
        {
            global::System.IO.File.WriteAllText(path, contents);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>True iff <paramref name="path"/> names an existing file
    /// (not a directory). Predicates don't return Result; pair with
    /// <see cref="read_to_string"/> when you actually want the contents.</summary>
    public static bool exists(string path) => global::System.IO.File.Exists(path);

    /// <summary>Read the file as UTF-8, splitting on newlines. Each line
    /// excludes the trailing `\n` (and `\r\n` on Windows). The final line
    /// is included even if it lacks a trailing newline. Empty file → empty
    /// list.</summary>
    public static Result<List<string>, IoError> read_lines(string path)
    {
        try
        {
            var lines = global::System.IO.File.ReadAllLines(path);
            return new ResultOk<List<string>, IoError>(
                new List<string>(System.Collections.Immutable.ImmutableArray.Create(lines)));
        }
        catch (Exception ex)
        {
            return new ResultErr<List<string>, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Append <paramref name="contents"/> to <paramref name="path"/>
    /// (UTF-8). Creates the file if it doesn't exist.</summary>
    public static Result<Unit, IoError> append_text(string path, string contents)
    {
        try
        {
            global::System.IO.File.AppendAllText(path, contents);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Delete the file at <paramref name="path"/>. Deleting a
    /// non-existent file is a no-op (matches .NET File.Delete and POSIX
    /// `rm -f`-ish semantics — programs that want a "missing" diagnostic
    /// guard with <see cref="exists"/> first).</summary>
    public static Result<Unit, IoError> delete(string path)
    {
        try
        {
            global::System.IO.File.Delete(path);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Size of the file in bytes. Errors (not found, permission
    /// denied, etc.) surface as Err.</summary>
    public static Result<int, IoError> size(string path)
    {
        try
        {
            var info = new global::System.IO.FileInfo(path);
            // FileInfo.Length is long; clamp to int. Files larger than
            // 2 GB are vanishingly rare for the v1 stdlib's audience and
            // can FFI to FileInfo directly when they matter.
            var len = info.Length;
            if (len > int.MaxValue)
            {
                return new ResultErr<int, IoError>(new IoError(
                    $"File.size: file '{path}' exceeds Int range ({len} bytes); use FFI for large files"));
            }
            return new ResultOk<int, IoError>((int)len);
        }
        catch (Exception ex)
        {
            return new ResultErr<int, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Atomic-where-supported rename. On the same filesystem this
    /// is the rename(2) primitive; across filesystems .NET falls back to
    /// copy + delete. Programs that need strict-atomic semantics across
    /// filesystem boundaries handle that themselves.</summary>
    public static Result<Unit, IoError> move(string from, string to)
    {
        try
        {
            global::System.IO.File.Move(from, to);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Copy the file. Existing destination is overwritten —
    /// matches the conventional "cp -f" default. Programs that want a
    /// "fail if exists" check guard with <see cref="exists"/> first.</summary>
    public static Result<Unit, IoError> copy(string from, string to)
    {
        try
        {
            global::System.IO.File.Copy(from, to, overwrite: true);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Read the file as raw bytes. Pairs with
    /// <see cref="Bytes.from_utf8"/> / <see cref="Bytes.to_utf8"/> when
    /// callers need to round-trip text through the binary form (e.g.
    /// hashing, network framing).</summary>
    public static Result<Bytes, IoError> read_bytes(string path)
    {
        try
        {
            return new ResultOk<Bytes, IoError>(
                new Bytes(global::System.IO.File.ReadAllBytes(path)));
        }
        catch (Exception ex)
        {
            return new ResultErr<Bytes, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Write raw bytes, overwriting any existing file. Default
    /// mode 0644 (matches WriteAllText).</summary>
    public static Result<Unit, IoError> write_bytes(string path, Bytes data)
    {
        try
        {
            global::System.IO.File.WriteAllBytes(path, data.Items);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }
}

/// <summary>
/// Filesystem directory operations. All carry !{io}. Directory listing,
/// creation (with parents-as-needed), and removal (with optional
/// recursive flag).
/// </summary>
public static class Directory
{
    public static bool exists(string path) => global::System.IO.Directory.Exists(path);

    /// <summary>Create the directory, including any missing parents.
    /// No-op if it already exists.</summary>
    public static Result<Unit, IoError> create(string path)
    {
        try
        {
            global::System.IO.Directory.CreateDirectory(path);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>List the entry names in the directory (file and
    /// subdirectory names; not full paths). Programs that want full
    /// paths join with <see cref="Path.join"/> per entry. The list
    /// order is filesystem-dependent and not promised stable across
    /// hosts.</summary>
    public static Result<List<string>, IoError> list(string path)
    {
        try
        {
            var entries = global::System.IO.Directory.GetFileSystemEntries(path);
            var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<string>(entries.Length);
            foreach (var e in entries)
            {
                builder.Add(global::System.IO.Path.GetFileName(e) ?? e);
            }
            return new ResultOk<List<string>, IoError>(new List<string>(builder.MoveToImmutable()));
        }
        catch (Exception ex)
        {
            return new ResultErr<List<string>, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Delete the directory. With <paramref name="recursive"/>
    /// = true, removes all contents; with false, requires the directory
    /// to be empty (matches POSIX rmdir / rm -r split).</summary>
    public static Result<Unit, IoError> delete(string path, bool recursive)
    {
        try
        {
            global::System.IO.Directory.Delete(path, recursive);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }
}

/// <summary>
/// Pure path-string helpers. None of these touch the filesystem — they
/// operate on the path string itself. For real file existence checks /
/// reads / writes, see <see cref="File"/>.
/// </summary>
public static class Path
{
    /// <summary>Join two path segments with the platform-appropriate
    /// separator. <c>Path.join("dir", "file.txt")</c> yields
    /// <c>"dir/file.txt"</c> on Unix or <c>"dir\\file.txt"</c> on
    /// Windows. The Go runtime does the same, so output round-trips
    /// across back ends on each platform.</summary>
    public static string join(string parent, string child)
        => global::System.IO.Path.Combine(parent, child);

    /// <summary>Directory portion of <paramref name="path"/>. Returns
    /// <c>None</c> when the path has no parent (a bare filename or
    /// the empty string).</summary>
    public static Option<string> parent(string path)
    {
        var dir = global::System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return new OptionNone<string>();
        return new OptionSome<string>(dir);
    }

    /// <summary>Final component of <paramref name="path"/>. Returns
    /// <c>None</c> for the empty string; otherwise the segment after
    /// the last separator.</summary>
    public static Option<string> file_name(string path)
    {
        var name = global::System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return new OptionNone<string>();
        return new OptionSome<string>(name);
    }

    /// <summary>File extension including the leading dot, e.g. <c>".ov"</c>.
    /// Returns <c>None</c> when the path has no extension.</summary>
    public static Option<string> extension(string path)
    {
        var ext = global::System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return new OptionNone<string>();
        return new OptionSome<string>(ext);
    }

    /// <summary>Replace (or add) the extension on <paramref name="path"/>.
    /// <paramref name="ext"/> may include or omit the leading dot;
    /// empty <paramref name="ext"/> strips any existing extension.</summary>
    public static string with_extension(string path, string ext)
    {
        var stripped = global::System.IO.Path.ChangeExtension(path, null) ?? path;
        if (string.IsNullOrEmpty(ext)) return stripped;
        return ext.StartsWith('.') ? stripped + ext : stripped + "." + ext;
    }

    /// <summary>True iff <paramref name="path"/> is rooted (absolute) per
    /// the host's path conventions.</summary>
    public static bool is_absolute(string path)
        => global::System.IO.Path.IsPathRooted(path);
}
