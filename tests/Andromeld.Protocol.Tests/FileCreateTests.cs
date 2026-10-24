using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static async Task CheckFileCreateAsync(AndromeldClient client, MockPhone server, string root)
    {
        const string name = "新建 #%.文件夹";
        string parent = root + "/create-tests";
        server.FileListings[parent] = [];
        await client.CreateDirectoryAsync(parent, name, CancellationToken.None);
        Check(server.CreateDirectoryRequests.Last() == (parent, name) &&
              (await client.ListAsync(parent, CancellationToken.None)).Single() is { IsDir: true, Name: name },
            "fs-mkdir sends the parent and Unicode name separately and creates a directory");

        int before = server.CreateDirectoryRequests.Count;
        foreach (string invalid in new[] { "", " ", ".", "..", " leading", "trailing ", "a/b", "a\\b", "a\0b", "a\nb", "a\u007fb", new string('a', 256), new string('中', 86) })
        {
            try { await client.CreateDirectoryAsync(parent, invalid, CancellationToken.None); throw new Exception("Invalid folder name accepted"); }
            catch (ArgumentException) { }
        }
        Check(server.CreateDirectoryRequests.Count == before, "invalid folder names are rejected before sending any request");
        string maximum = new('中', 85);
        await client.CreateDirectoryAsync(parent, maximum, CancellationToken.None);
        Check(server.CreateDirectoryRequests.Last().Name == maximum, "folder names accept the 255-byte UTF-8 boundary");

        try { await client.CreateDirectoryAsync(parent, name, CancellationToken.None); throw new Exception("Duplicate folder accepted"); }
        catch (IOException ex) { Check(ex.Message.Contains("eexist"), "fs-mkdir surfaces existing-name errors without replacing the entry"); }
        server.FileMkdirError = "eacces";
        try { await client.CreateDirectoryAsync(parent, "denied", CancellationToken.None); throw new Exception("Create permission failure accepted"); }
        catch (IOException ex) { Check(ex.Message.Contains("eacces"), "fs-mkdir surfaces Android permission errors"); }
        finally { server.FileMkdirError = null; }

        server.FileMkdirReplyOperation = "delete";
        try { await client.CreateDirectoryAsync(parent, "wrong-reply", CancellationToken.None); throw new Exception("Wrong operation reply accepted"); }
        catch (IOException) { Check(true, "fs-mkdir rejects a successful reply for a different operation"); }
        finally { server.FileMkdirReplyOperation = "mkdir"; }
        server.FileListings.Clear();
    }
}
