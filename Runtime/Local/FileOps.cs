using System.IO;
using System.Threading;

namespace Ecanakli.SaveSystem
{
    /// <summary>File system seam used by AtomicFileStorage; absolute paths only. Test fakes inject faults and record sleeps.</summary>
    internal interface IFileOps
    {
        /// <summary>True when a file exists at path.</summary>
        bool FileExists(string path);

        /// <summary>True when a directory exists at path.</summary>
        bool DirectoryExists(string path);

        /// <summary>Reads the whole file.</summary>
        byte[] ReadAllBytes(string path);

        /// <summary>Creates or truncates the file, writes bytes and flushes to disk (FileStream.Flush(true)).</summary>
        void WriteAllBytesFlushed(string path, byte[] bytes);

        /// <summary>Renames a file; throws when the destination exists.</summary>
        void MoveFile(string sourcePath, string destinationPath);

        /// <summary>Deletes a file; no-op when it does not exist.</summary>
        void DeleteFile(string path);

        /// <summary>Creates a directory and its parents.</summary>
        void CreateDirectory(string path);

        /// <summary>Renames a directory; throws when the destination exists.</summary>
        void MoveDirectory(string sourcePath, string destinationPath);

        /// <summary>Deletes a directory recursively.</summary>
        void DeleteDirectory(string path);

        /// <summary>File names (not paths) directly inside a directory.</summary>
        string[] GetFileNames(string directoryPath);

        /// <summary>Directory names (not paths) directly inside a directory.</summary>
        string[] GetDirectoryNames(string directoryPath);

        /// <summary>Blocks the calling thread; used only by Windows replace retries.</summary>
        void Sleep(int milliseconds);
    }

    /// <summary>IFileOps over System.IO.</summary>
    internal sealed class SystemFileOps : IFileOps
    {
        /// <summary>Shared stateless instance.</summary>
        public static readonly SystemFileOps Instance = new SystemFileOps();

        private const int WriteBufferSize = 4096;

        public bool FileExists(string path)
        {
            return File.Exists(path);
        }

        public bool DirectoryExists(string path)
        {
            return Directory.Exists(path);
        }

        public byte[] ReadAllBytes(string path)
        {
            return File.ReadAllBytes(path);
        }

        public void WriteAllBytesFlushed(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, WriteBufferSize, FileOptions.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath)
        {
            File.Move(sourcePath, destinationPath);
        }

        public void DeleteFile(string path)
        {
            File.Delete(path);
        }

        public void CreateDirectory(string path)
        {
            Directory.CreateDirectory(path);
        }

        public void MoveDirectory(string sourcePath, string destinationPath)
        {
            Directory.Move(sourcePath, destinationPath);
        }

        public void DeleteDirectory(string path)
        {
            Directory.Delete(path, true);
        }

        public string[] GetFileNames(string directoryPath)
        {
            string[] paths = Directory.GetFiles(directoryPath);
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = Path.GetFileName(paths[i]);
            }

            return paths;
        }

        public string[] GetDirectoryNames(string directoryPath)
        {
            string[] paths = Directory.GetDirectories(directoryPath);
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = Path.GetFileName(paths[i]);
            }

            return paths;
        }

        public void Sleep(int milliseconds)
        {
            Thread.Sleep(milliseconds);
        }
    }
}
