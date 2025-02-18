﻿using Files.Shared.Extensions;
using FilesFullTrust.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace FilesFullTrust.MessageHandlers
{
    [SupportedOSPlatform("Windows10.0.10240")]
    public class RecycleBinHandler : Disposable, IMessageHandler
    {
        private IList<FileSystemWatcher> binWatchers;
        private PipeStream connection;

        public void Initialize(PipeStream connection)
        {
            this.connection = connection;

            // Create shell COM object and get recycle bin folder
            using var recycler = new ShellFolder(Shell32.KNOWNFOLDERID.FOLDERID_RecycleBinFolder);
            ApplicationData.Current.LocalSettings.Values["RecycleBin_Title"] = recycler.Name;

            StartRecycleBinWatcher();
        }

        private void StartRecycleBinWatcher()
        {
            // Create filesystem watcher to monitor recycle bin folder(s)
            // SHChangeNotifyRegister only works if recycle bin is open in explorer :(
            binWatchers = new List<FileSystemWatcher>();
            var sid = WindowsIdentity.GetCurrent().User.ToString();
            foreach (var drive in DriveInfo.GetDrives())
            {
                var recyclePath = Path.Combine(drive.Name, "$RECYCLE.BIN", sid);
                if (drive.DriveType == DriveType.Network || !Directory.Exists(recyclePath))
                {
                    continue;
                }
                var watcher = new FileSystemWatcher
                {
                    Path = recyclePath,
                    Filter = "*.*",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                watcher.Created += RecycleBinWatcher_Changed;
                watcher.Deleted += RecycleBinWatcher_Changed;
                watcher.EnableRaisingEvents = true;
                binWatchers.Add(watcher);
            }
        }

        public async Task ParseArgumentsAsync(PipeStream connection, Dictionary<string, object> message, string arguments)
        {
            switch (arguments)
            {
                case "RecycleBin":
                    var binAction = (string)message["action"];
                    await ParseRecycleBinActionAsync(connection, message, binAction);
                    break;
            }
        }

        private async Task ParseRecycleBinActionAsync(PipeStream connection, Dictionary<string, object> message, string action)
        {
            switch (action)
            {
                case "Empty":
                    // Shell function to empty recyclebin
                    Shell32.SHEmptyRecycleBin(IntPtr.Zero, null, Shell32.SHERB.SHERB_NOCONFIRMATION | Shell32.SHERB.SHERB_NOPROGRESSUI);
                    break;

                case "Query":
                    var binForDrive = message.Get("drive", "");
                    var responseQuery = new ValueSet();
                    Win32API.SHQUERYRBINFO queryBinInfo = new Win32API.SHQUERYRBINFO();
                    queryBinInfo.cbSize = Marshal.SizeOf(queryBinInfo);
                    var res = Win32API.SHQueryRecycleBin(binForDrive, ref queryBinInfo);
                    if (res == HRESULT.S_OK)
                    {
                        var numItems = queryBinInfo.i64NumItems;
                        var binSize = queryBinInfo.i64Size;
                        responseQuery.Add("HasRecycleBin", true);
                        responseQuery.Add("NumItems", numItems);
                        responseQuery.Add("BinSize", binSize);
                        await Win32API.SendMessageAsync(connection, responseQuery, message.Get("RequestID", (string)null));
                    }
                    else
                    {
                        responseQuery.Add("HasRecycleBin", false);
                        responseQuery.Add("NumItems", 0);
                        responseQuery.Add("BinSize", 0);
                        await Win32API.SendMessageAsync(connection, responseQuery, message.Get("RequestID", (string)null));
                    }
                    break;

                default:
                    break;
            }
        }

        private async void RecycleBinWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"Recycle bin event: {e.ChangeType}, {e.FullPath}");
            if (e.Name.StartsWith("$I", StringComparison.Ordinal))
            {
                // Recycle bin also stores a file starting with $I for each item
                return;
            }
            if (connection?.IsConnected ?? false)
            {
                var response = new ValueSet()
                {
                    { "FileSystem", @"Shell:RecycleBinFolder" },
                    { "Path", e.FullPath },
                    { "Type", e.ChangeType.ToString() }
                };
                if (e.ChangeType == WatcherChangeTypes.Created)
                {
                    using var folderItem = SafetyExtensions.IgnoreExceptions(() => new ShellItem(e.FullPath));
                    if (folderItem == null) return;
                    var shellFileItem = ShellFolderExtensions.GetShellFileItem(folderItem);
                    response["Item"] = JsonConvert.SerializeObject(shellFileItem);
                }
                // Send message to UWP app to refresh items
                await Win32API.SendMessageAsync(connection, response);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var watcher in binWatchers)
                {
                    watcher.Dispose();
                }
            }
        }
    }
}
