using System.Net;
using System.IO;
using NAudio.Wave;
using System.Threading;
using Microsoft.Bing.Speech;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System.Collections.Generic;

/// <summary>
/// Converts Media Foundation AV files (e.g. MP4) to WAV format (if required) and cuts into segments of the specified length (in seconds).
/// Saves the resulting .wav files to Azure Blob Storage.
/// Makes use of the NAudio library for .NET: https://github.com/naudio/NAudio
/// </summary>
/// <param name="avBlob">Incoming stream from Blob Storage input trigger.</param>
/// <param name="context">ExecutionContext for referencing the local FunctionDirectory filepath.</param>
/// <param name="name">Full name (including extension) of the trigger file from Blob Storage.</param>
/// <param name="log">Log for events.</param>
public static async Task Run(Stream avBlob, Microsoft.Azure.WebJobs.ExecutionContext context, string name, TraceWriter log)
{
    log.Info($"C# Blob trigger function received blob\n Name:{name} \n Size: {avBlob.Length} bytes");
    
	int pos = name.LastIndexOf(".") + 1;
	string filename = name.Substring(0, pos - 1);
	string fileExtension = name.Substring(pos, name.Length - pos);
	string parentFilepath = $"{context.FunctionDirectory}\\MediaFiles\\{filename}";
	string rawFilepath = $"{parentFilepath}\\{name}";
    string wavFilepath = rawFilepath;
    string segmentFolderPath = parentFilepath + "\\MediaSegments";

    // Create local directory to store the file and all its segments
    System.IO.Directory.CreateDirectory(parentFilepath);
 
    // Save file to local directory
    FileStream fileStream = null;
    using(fileStream = File.Create(rawFilepath, (int)avBlob.Length))
    {
		using (MemoryStream ms = new MemoryStream())
		{
			avBlob.CopyTo(ms);
			ms.WriteTo(fileStream);
		}
    }

    // Convert AV files to .wav for transcription, if necessary (at the time of writing, Bing Speech Detection accepts only .wav formatted audio)
	if(!fileExtension.Equals("wav"))
	{
        log.Info($"Converting .{fileExtension} format to .wav");
        wavFilepath = $"{parentFilepath}\\{filename}.wav";
        try
        {
            ConvertToWav(wavFilepath, rawFilepath);
            log.Info($"Conversion successful");
        }
	    catch
	    {
		    log.Info($"Could not process file. Filetype was: {fileExtension}");
	    }
	}

    using (var waveFileReader = new WaveFileReader(wavFilepath))
    {
        log.Info("Attempting to segment audio");
        try
        {
            SegmentAudio(waveFileReader, segmentFolderPath, filename);
            log.Info("Audio segmenting complete");
        }
        catch (Exception ex)
        {
            log.Info("Audio segmenting failed: " + ex.Message);
        }
    }

    try 
    {
        log.Info("Sending segments to blob");
        await SendFilesToBlob(segmentFolderPath);
        log.Info("Saved segments to blob");
    }
    catch (Exception ex)
    {
        log.Info("Failed to save file to Blob: " + ex.Message);
    }

    // Delete local files after processing
    Directory.Delete($"{context.FunctionDirectory}\\MediaFiles\\", true);
}

/// <summary>
/// Extracts audio from AV files and saves to local .wav file. 
/// Supports a number of input formats e.g. MP3, MP4, WMA, AAC etc. - any <a href="https://msdn.microsoft.com/en-us/library/windows/desktop/dd757927(v=vs.85).aspx">Media Foundation filetype</a>.
/// </summary>
/// <param name="wavFilepath">Desired filepath for generated .wav file.</param>
/// <param name="rawFilepath">Filepath for raw AV file.</param>
private static void ConvertToWav(string wavFilepath, string rawFilepath)
{
	using(var reader = new MediaFoundationReader(rawFilepath))
	{
		WaveFileWriter.CreateWaveFile(wavFilepath, reader);
	}
}

/// <summary>
/// Cuts audio file (.wav) into segments of length specified in the App Settings.
/// </summary>
/// <param name="waveFileReader">Reader for .wav file.</param>
/// <param name="segmentFolderPath">Filepath for storage of created audio segments.</param>
/// <param name="filename">Name of the original AV file for deriving segment names.</param>
private static void SegmentAudio(WaveFileReader waveFileReader, string segmentFolderPath, string filename)
{
    System.IO.Directory.CreateDirectory(segmentFolderPath);
    
	int audioLengthSecs = (int)waveFileReader.SampleCount / waveFileReader.WaveFormat.SampleRate;
    int segmentLengthSecs = Convert.ToInt16(System.Environment.GetEnvironmentVariable("VideoSegmentDurationSecs"));

    if (segmentLengthSecs > audioLengthSecs) 
    {
		segmentLengthSecs = audioLengthSecs;
	}
		
    int numberOfSegments = Convert.ToInt16(Math.Ceiling((double)audioLengthSecs / (double)segmentLengthSecs));     
        
    int bytesPerSec = waveFileReader.WaveFormat.AverageBytesPerSecond;

    long startPos = 0;
    long endPos = audioLengthSecs * bytesPerSec;
    
	// Segment audio into separate .wav files
    //TODO: Overlap segments slightly to avoid missing words during transcription
    for (int i = 0; i < numberOfSegments; i++)
    {
        long segmentEndPos = startPos + (segmentLengthSecs * bytesPerSec);
        if (segmentEndPos > endPos)
        {
            segmentEndPos = endPos;
        }
        string segmentFilepath = $"{segmentFolderPath}\\{filename}_segment_{i}.wav";

        using (var waveFileWriter = new WaveFileWriter(segmentFilepath, waveFileReader.WaveFormat))
        {
            WriteWavSegment(waveFileReader, waveFileWriter, startPos, endPos);
        }

		startPos = segmentEndPos;
    }
}

/// <summary>
/// Writes audio segments to .wav files in local storage.
/// </summary>
/// <param name="reader">Reader for .wav file.</param>
/// <param name="writer">Writer for .wav file.</param>
/// <param name="startPos">Segment start position.</param>
/// <param name="endPos">Segment end position.</param>
private static void WriteWavSegment(WaveFileReader reader, WaveFileWriter writer, long startPos, long endPos)
{
    reader.Position = startPos;

    // make sure that buffer is sized to a multiple of our WaveFormat.BlockAlign.

    // WaveFormat.BlockAlign = channels * (bits / 8), so for 16 bit stereo wav it will be 4096 bytes

    var buffer = new byte[reader.BlockAlign * 1024];

    while (reader.Position < endPos)
    {
        long bytesRequired = endPos - reader.Position;

        if (bytesRequired > 0)
        {
            int bytesToRead = (int)Math.Min(bytesRequired, buffer.Length);

            int bytesRead = reader.Read(buffer, 0, bytesToRead);

            if (bytesRead > 0)
            {
                writer.Write(buffer, 0, bytesToRead);
            }
        }
    }
}

/// <summary>
/// Saves files to Blob Storage.
/// </summary>
/// <param name="segmentFolderPath">Filepath for extracted .wav segments.</param>
private static async Task SendFilesToBlob(string segmentFolderPath)
{
    CloudStorageAccount storageAccount = null;
    CloudBlobContainer cloudBlobContainer = null;
    string storageConnectionString = System.Configuration.ConfigurationManager.ConnectionStrings["OutputBlobConnStr"].ConnectionString;

    // Find all extracted segments
    string[] segmentFileEntries = Directory.GetFiles(segmentFolderPath);

    if (CloudStorageAccount.TryParse(storageConnectionString, out storageAccount))
    {
        // Create the CloudBlobClient that represents the Blob storage endpoint for the storage account
        CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();

        // Get container reference/create container if it doesn't already exist
        cloudBlobContainer = cloudBlobClient.GetContainerReference(System.Environment.GetEnvironmentVariable("AudioSegmentBlobContainerName"));
        await cloudBlobContainer.CreateIfNotExistsAsync();

        // Set the permissions so the blobs are public
        BlobContainerPermissions permissions = new BlobContainerPermissions
        {
            PublicAccess = BlobContainerPublicAccessType.Blob
        };
        await cloudBlobContainer.SetPermissionsAsync(permissions);

        // Write each segment file to blob for processing
        foreach (string segmentFileName in segmentFileEntries)
        {
            int slashpos = segmentFileName.LastIndexOf("\\") + 1;
            string segmentName = segmentFileName.Substring(slashpos, segmentFileName.Length - slashpos);
            // Get a reference to the blob address, then upload the file to the blob
            // Use the value of localFileName for the blob name
            CloudBlockBlob cloudBlockBlob = cloudBlobContainer.GetBlockBlobReference(segmentName);
            await cloudBlockBlob.UploadFromFileAsync(segmentFileName);
        }
    }
}