using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Net.Http;
using Newtonsoft.Json;
using System.Text;
using Oxide.Game.Rust.Libraries;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Plugins;

namespace Oxide.Ext.Rustigate
{
    public class RustigateDiscordPost
    {
        #region DiscordAPI

        private class DiscordJsonFooter
        {
            [JsonProperty("text")]
            public string Text { get; set; }
        }

        private class DiscordJsonField
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("value")]
            public string Value { get; set; }
        }

        private class DiscordJsonEmbed
        {
            [JsonProperty("title")]
            public string Title { get; set; }

            [JsonProperty("description")]
            public string Description { get; set; }

            [JsonProperty("color")]
            public string Color { get; set; }

            [JsonProperty("fields")]
            public List<DiscordJsonField> Fields { get; set; }

            [JsonProperty("footer")]
            public DiscordJsonFooter Footer { get; set; }
        }

        private class DiscordJsonMessage
        {
            [JsonProperty("content")]
            public string Content { get; set; }

            [JsonProperty("embeds")]
            public List<DiscordJsonEmbed> Embeds { get; set; }

            public DiscordJsonMessage() { }

            //i mean its kinda rough but this plugin only makes a report a single way
            //theres plenty of other discord API plugins out there, this wasnt ment to be used outside of this plugin
            public DiscordJsonMessage(string EmbedTitle, string EmbedDescription, string FieldName, string FieldValue, string FooterText)
            {
                List<DiscordJsonField> Fields = new List<DiscordJsonField>();
                Fields.Add(new DiscordJsonField()
                {
                    Name = FieldName,
                    Value = FieldValue
                });

                List<DiscordJsonEmbed> DiscordEmbeds = new List<DiscordJsonEmbed>();
                DiscordEmbeds.Add(new DiscordJsonEmbed()
                {
                    Title = EmbedTitle,
                    Description = EmbedDescription,
                    Color = "16711680",
                    Fields = Fields,
                    Footer = new DiscordJsonFooter()
                    {
                        Text = FooterText
                    }
                });

                //Content = MsgContent;
                Embeds = DiscordEmbeds;
            }

            public void AddDiscordEmbedField(string FieldName, string FieldValue, int EmbedIDX = 0, int FieldIDX = 0)
            {
                DiscordJsonField NewField = new DiscordJsonField()
                {
                    Name = FieldName,
                    Value = FieldValue
                };
                Embeds[EmbedIDX]?.Fields.Insert(FieldIDX, NewField);
            }
        }

        private class DiscordPostData
        {
            public ZippedDemoFiles DiscordPostFile = new ZippedDemoFiles();
            public DiscordJsonMessage DiscordJsonMessage = new DiscordJsonMessage();

            public long GetAllFileSize()
            {
                return DiscordPostFile.ZipFileData.LongLength;
            }
            public DiscordPostData() { }
        }
        
        public class DiscordReportInfo
        {
            public string AttackerID;
            public string AttackerName;
            public string ReporterName;
            public string ReportSubject;
            public string ReportMessage;

            public DiscordReportInfo(string attackerID, string attackerName, string reporterName, string reportSubject, string reportMessage)
            {
                AttackerID = attackerID;
                AttackerName = attackerName;
                ReporterName = reporterName;
                ReportSubject = reportSubject;
                ReportMessage = reportMessage;
            }
        }

        //as of 3-12-2023, these are the discord boost teir, file attachment limits, with the last one used for debug purposes only
        private static long[] DiscordAttachmentServerLimits = new long[5]
            {
                8000000,
                8000000,
                50000000,
                100000000,
                9999999999
            };

        #endregion

        #region Config

        private Int32 DiscordServerBoostTier = 0;
        private string DiscordWebhookURL = "";
        private string CurrentServerIP = "localhost";
        private bool bUploadDemosToDiscord = true;

        public void LoadDiscordConfig(Int32 discordServerBoostTier, string discordWebhookURL, bool buploadDemosToDiscord, string currentServerIP)
        {
            DiscordServerBoostTier = discordServerBoostTier;
            DiscordWebhookURL = discordWebhookURL;
            CurrentServerIP = currentServerIP;
            bUploadDemosToDiscord = buploadDemosToDiscord;
        }

        #endregion

        #region ZipCrap
        private class ZippedDemoFiles
        {
            public List<string> Filenames = new List<string>();
            public List<string> CompressedFilenames = new List<string>();
            public byte[] ZipFileData;
            public string ZipFileName;

            public ZippedDemoFiles() { }
        }

        static private ZippedDemoFiles CompressDemoFiles(List<string> FileNames, string CompressedFileNamePrefix)
        {
            ZippedDemoFiles ReturnValue = new ZippedDemoFiles();
            using (MemoryStream ms = new MemoryStream())
            {
                using (ZipArchive NewZipArchive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    DateTime OldestFileTime = DateTime.Now; //everything is older then now
                    DateTime NewestFileTime = DateTime.FromBinary(0); //everything is newer then epoch
                    ReturnValue.Filenames = FileNames;
                    for (int i = 0; i < FileNames.Count; i++)
                    {
                        string FileName = FileNames[i];
                        FileInfo fileInfo = new FileInfo(FileName);
                        DateTime FileCreationTime = fileInfo.LastWriteTime;
                        string CompressedFileName = $"{CompressedFileNamePrefix}_{FileCreationTime.ToShortDateString()}_{FileCreationTime.ToShortTimeString()}-{i}.dem";
                        CompressedFileName = Path.GetInvalidFileNameChars().Aggregate(CompressedFileName, (current, c) => current.Replace(c, '_'));
                        NewZipArchive.CreateEntryFromFile(FileName, CompressedFileName, CompressionLevel.Optimal);
                        ReturnValue.CompressedFilenames.Add(CompressedFileName);

                        if (FileCreationTime < OldestFileTime)
                        {
                            OldestFileTime = FileCreationTime;
                        }

                        if (FileCreationTime > NewestFileTime)
                        {
                            NewestFileTime = FileCreationTime;
                        }
                    }

                    if (OldestFileTime == NewestFileTime)
                    {
                        ReturnValue.ZipFileName = $"{CompressedFileNamePrefix}_{NewestFileTime.ToShortDateString()}_{NewestFileTime.ToShortTimeString()}.zip";
                    }
                    else
                    {
                        ReturnValue.ZipFileName = $"{CompressedFileNamePrefix}_{OldestFileTime.ToShortDateString()}_{OldestFileTime.ToShortTimeString()}-{NewestFileTime.ToShortDateString()}_{NewestFileTime.ToShortTimeString()}.zip";
                    }
                }
                ReturnValue.ZipFileData = ms.ToArray();
            }
            return ReturnValue;
        }
        #endregion

        //https://www.aspnetmonsters.com/2016/08/2016-08-27-httpclientwrong/
        private static HttpClient client = new HttpClient();
        public async void UploadDiscordReportAsync(List<string> DemoFilenames, DiscordReportInfo discordReportInfo, List<string> VictimNames, Action<string> debugcallback)
        {
            try
            {
                if(DiscordWebhookURL == "")
                {
                    debugcallback("DiscordWebhookURL is empty!");
                    return;
                }

                ///@ ZipArchive: This property cannot be retrieved when the mode is set to Create, or the mode is set to Update and the entry has been opened.
                ///is about the dumbest shit i ever herd of https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchiveentry.length?view=net-7.0
                ///which would mean i have to compress everyfile, read what size it is when compressed in the zip
                ///then iterate again building split zips recompressing the same files again, uploading every 8MiB...
                ///
                ///i had written all this with GZip, where we still compress everything but now we know the size,
                ///so just send it out in sets of 8MiB but now we need to be under 10 attachments
                ///however it was very spammy in discord, whole screen would get plastered in file attachments, it was very messy
                ///and the admin has to download each gz individually and extract each one..
                ///
                ///for ZipArchive i tried writing the final file by splitting the buffer every x bytes, winrar or 7zip dint know what the fuck it was trying to read
                ///but with python you could piece it back together to form the original zip just fine... dont want admins having to do that, its just stupid
                ///
                ///also tried taking the original zipped file stream, and tried copying it to a new zipfilestream with compression turned off, theres an issue with this:
                ///in ZipArchiveEntry @ GetDataDecompressor() which is used to read the stream inside of the zip, it decompresses it first...
                ///no way to access _compressedBytes directly...
                ///
                ///and i dont want to have to include DotNetZip with the project...
                ///

                //like explained above, we zip up all the demofiles so we can know their compressed filesize
                //knowing this we can split the zip files based on MaxTotalAttachmentSize and send them threw their own discord post
                //sadly this means wasting cpu, recompressing the same file again to create the splitted archive
                //todo: can you merge DotNetZip into the same .dll as the project ???

                long MaxTotalAttachmentSize = DiscordAttachmentServerLimits[DiscordServerBoostTier];
                List<string> MassiveFiles = new List<string>();
                List<ZippedDemoFiles> FileAttachments = new List<ZippedDemoFiles>();

                if (bUploadDemosToDiscord)
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (ZipArchive masterzip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                        {
                            for (int i = 0; i < DemoFilenames.Count; i++)
                            {
                                masterzip.CreateEntryFromFile(DemoFilenames[i], "0");
                            }
                        }

                        //now we can read ZipArchiveEntry.CompressedLength properly...
                        using (ZipArchive masterzip = new ZipArchive(ms, ZipArchiveMode.Read))
                        {
                            long TestArchiveSize = 0;
                            List<string> DemoFilesToZip = new List<string>();
                            for (int i = 0; i < masterzip.Entries.Count; i++)
                            {
                                ZipArchiveEntry zfile = masterzip.Entries[i];
                                long zfileSize = zfile.CompressedLength;

                                if (zfileSize > MaxTotalAttachmentSize)
                                {
                                    MassiveFiles.Add(DemoFilenames[i]);
                                    continue;
                                }

                                long FutureTestArchiveSize = TestArchiveSize + zfileSize;
                                if (FutureTestArchiveSize > MaxTotalAttachmentSize)
                                {
                                    //current post is full, make new one
                                    ZippedDemoFiles zippedDemoFiles = CompressDemoFiles(DemoFilesToZip, discordReportInfo.AttackerName);
                                    FileAttachments.Add(zippedDemoFiles);

                                    TestArchiveSize = 0;
                                    DemoFilesToZip.Clear();
                                }

                                DemoFilesToZip.Add(DemoFilenames[i]);
                                TestArchiveSize += zfileSize;
                            }

                            //last file, todo: write all this better lmao
                            if (DemoFilesToZip.Count > 0)
                            {
                                //current post is full, make new one
                                ZippedDemoFiles zippedDemoFiles = CompressDemoFiles(DemoFilesToZip, discordReportInfo.AttackerName);
                                FileAttachments.Add(zippedDemoFiles);
                            }
                        }
                    }
                }

                string ServerIP = CurrentServerIP;

                //the reason why we post each file as a seperate message is for looks
                //if all the attachments are in their own section and then right after that is the text report
                //its more readable in discord, instead of posting a text report for each file attachment
                //one report from a player should be one text post, along side its file attachments
                foreach (ZippedDemoFiles FileAttachment in FileAttachments)
                {
                    using (var formData = new MultipartFormDataContent())
                    {
                        string AttachmentFilename = FileAttachment.ZipFileName;

                        //i took this out cause discord does it for you, but really should keep this... 
                        //AttachmentFilename = Path.GetInvalidFileNameChars().Aggregate(AttachmentFilename, (current, c) => current.Replace(c, '!')); 

                        formData.Add(new ByteArrayContent(FileAttachment.ZipFileData), $"file1", AttachmentFilename);
                        var fileresponse = await client.PostAsync(DiscordWebhookURL, formData);

                        // ensure the request was a success
                        if (!fileresponse.IsSuccessStatusCode)
                        {
                            //if we got a file too large, chances are DiscordServerBoostTier is incorrect, or the server's boost teir just expired
                            //if this is true, we need to reset it back to 0, and send a message saying theres trouble
                            if (fileresponse.StatusCode.ToString() == "RequestEntityTooLarge")
                            {
                                DiscordServerBoostTier = 0;

                                var requeststr = new { content = $"\n\n\n ** !! DiscordServerBoostTier is set incorrectly for {ServerIP}, Some attached files might be missing for this report! Defaulting to {DiscordAttachmentServerLimits[0]/(1000*1000)}MB for future reports !! ** \n\n\n" };
                                StringContent errorStringContent = new StringContent(JsonConvert.SerializeObject(requeststr), Encoding.UTF8, "application/json");
                                
                                await client.PostAsync(DiscordWebhookURL, errorStringContent);
                                debugcallback("DiscordServerBoostTier is set incorrectly, defaulting to teir 0!");
                            }

                            debugcallback(fileresponse.ReasonPhrase);
                            debugcallback(await fileresponse.Content.ReadAsStringAsync());
                            //return; //keep going, if theres trouble the admin can resort to manual file transfer
                        }
                    }
                }

                string Victims = String.Join(", ", VictimNames);
                string EmbedTitle = $"{discordReportInfo.AttackerName}[{discordReportInfo.AttackerID}] was reported by {discordReportInfo.ReporterName} for {discordReportInfo.ReportSubject}: {discordReportInfo.ReportMessage}";

                string EmbedDescription = "";
                foreach (string DemoFilename in DemoFilenames)
                {
                    if (!MassiveFiles.Contains(DemoFilename))
                    {
                        EmbedDescription += $"{DemoFilename}\n";
                    }
                }

                DiscordJsonMessage DiscordMessage = new DiscordJsonMessage(EmbedTitle, EmbedDescription, "Victims:", Victims, $"from server: {ServerIP}");

                if (MassiveFiles.Count > 0)
                {
                    string MassiveFileWarning = "The following files are too big for discord upload:";
                    string MassiveFileDescription = "";
                    foreach (var MassiveFile in MassiveFiles)
                    {
                        MassiveFileDescription += $"{MassiveFile}\n";
                    }

                    DiscordMessage.AddDiscordEmbedField(MassiveFileWarning, MassiveFileDescription);
                }

                StringContent stringContent = new StringContent(JsonConvert.SerializeObject(DiscordMessage), Encoding.UTF8, "application/json");
                var txtresponse = await client.PostAsync(DiscordWebhookURL, stringContent);

                // ensure the request was a success
                if (!txtresponse.IsSuccessStatusCode)
                {
                    debugcallback(txtresponse.ReasonPhrase);
                    debugcallback(await txtresponse.Content.ReadAsStringAsync());
                    return;
                }
            }

            //@note: theres a "try" all the way above
            //and yea this is fukin stupid but it allows very simple error print in console while i figure this out so fuck it man
            catch (Exception e)
            {
                debugcallback(e.Message);
                debugcallback(e.StackTrace);
                throw;
            }
        }
    }
}
