#r "Newtonsoft.Json"
#load "CognitiveServicesAuthorizationProvider.cs"
#load "BingSpeechResult.cs"
#load "Phrase.cs"
using Newtonsoft.Json.Linq;
using Microsoft.Bing.Speech;
using System.Threading;
using System.Net;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;

private static readonly CancellationTokenSource cts = new CancellationTokenSource();
private static readonly Task CompletedTask = Task.FromResult(true);
private static string chunkFilename;
private static TraceWriter logWriter;

/// <summary>
/// Sends audio content for transcription using Bing Speech to Text Service Library - more detail <a href="https://docs.microsoft.com/en-us/azure/cognitive-services/speech/getstarted/getstartedcsharpservicelibrary">here</a>.
/// Transcribed phrases (defined by Bing Speech) are stored in Cosmos DB.
/// </summary>
/// <param name="audioChunkBlob">Incoming stream from Blob Storage input trigger.</param>
/// <param name="name">Full name (including extension) of the trigger file from Blob Storage.</param>
/// <param name="log">Log for events.</param>
public static async Task Run(Stream audioChunkBlob, string name, TraceWriter log)
{
    log.Info($"C# Blob trigger function Processed blob\n Name:{name} \n Size: {audioChunkBlob.Length} Bytes");
    logWriter = log;
    int pos = name.LastIndexOf(".") + 1;
	chunkFilename = name.Substring(0, pos - 1);

    var preferences = new Preferences("en-GB", new Uri(System.Environment.GetEnvironmentVariable("BingSpeechClientEndpointURL")), new CognitiveServicesAuthorizationProvider(System.Environment.GetEnvironmentVariable("BingSpeechKey")));

    using (var speechClient = new SpeechClient(preferences))
    {
        log.Info("Sending stream for transcription");
        
        try
        {
            speechClient.SubscribeToRecognitionResult(OnRecognitionResult);

            // Pass audio content to stream and upload using dummy metadata
            var deviceMetadata = new DeviceMetadata(DeviceType.Near, DeviceFamily.Desktop, NetworkType.Ethernet, OsName.Windows, "1607", "Dell", "T3600");
            var applicationMetadata = new ApplicationMetadata("SampleApp", "1.0.0");
            var requestMetadata = new RequestMetadata(Guid.NewGuid(), deviceMetadata, applicationMetadata, "SampleAppService");

            await speechClient.RecognizeAsync(new SpeechInput(audioChunkBlob, requestMetadata), cts.Token);
        }
        catch(Exception ex)
        {
            log.Info($"Error transcribing audio: {ex.Message}");
        }
    }
}

/// <summary>
/// Invoked every time Bing Speech returns a phrase recognition result (or results).
/// Each result contains information from a single 'phrase', as defined by Bing using silences in the audio stream.
/// Saves result as JSON document to Cosmos DB.
/// </summary>
/// <param name="args">Phrase recognition result returned from the Bing Speech Service.</param>
/// <returns>
/// A task
/// </returns>
private static Task OnRecognitionResult(RecognitionResult args)
{
    var response = args;

    if (response != null) 
    {
        BingSpeechResult transcriptionResult = new BingSpeechResult
        {
            Id = Guid.NewGuid().ToString(),
            Filename = chunkFilename
        };

        transcriptionResult.Phrases = new List<Phrase>();
        
        transcriptionResult.RecognitionStatus = response.RecognitionStatus.ToString();
        
        if (response.Phrases != null)
        {
            foreach (var result in response.Phrases)
            {
                transcriptionResult.Phrases.Add(new Phrase
                {
                    ConfidenceRating = result.Confidence.ToString(),
                    Text = result.DisplayText,
                    SecsFromFileStart = (float)((long)result.MediaTime/(double)10000000)
                });
            }
        }

        CreateDocIfNotExists(transcriptionResult).Wait();
    }

    return CompletedTask;
}

/// <summary>
/// Saves transcription result to Cosmos DB as JSON document.
/// </summary>
/// <param name="transcriptionResult">Phrase recognition result.</param>
private static async Task CreateDocIfNotExists(BingSpeechResult transcriptionResult)
{
    string cosmosKey = System.Environment.GetEnvironmentVariable("CosmosKey");
    string cosmosURI = System.Environment.GetEnvironmentVariable("CosmosURI");
    string databaseName = System.Environment.GetEnvironmentVariable("CosmosDBName");
    string collectionName = System.Environment.GetEnvironmentVariable("CosmosCollectionName");

    using (DocumentClient cosmosClient = new DocumentClient(new Uri(cosmosURI), cosmosKey))
    {
        try
        {
            await cosmosClient.CreateDatabaseIfNotExistsAsync(new Database { Id = databaseName });
            await cosmosClient.CreateDocumentCollectionIfNotExistsAsync(UriFactory.CreateDatabaseUri(databaseName), new DocumentCollection { Id = collectionName });
            await cosmosClient.CreateDocumentAsync(UriFactory.CreateDocumentCollectionUri(databaseName, collectionName), transcriptionResult);

            logWriter.Info($"Saved result to Cosmos, document ID: {transcriptionResult.Id}");  
        }
        catch (Exception ex)
        {
            logWriter.Info($"Error saving to Cosmos: {ex.Message}");  
        }
    }
}