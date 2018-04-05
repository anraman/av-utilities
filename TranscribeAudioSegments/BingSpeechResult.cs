#r "Newtonsoft.Json"
using System.Collections.Generic;
using Newtonsoft.Json;

public class BingSpeechResult
{
    [JsonProperty(PropertyName = "id")]
    public string Id { get; set; }
    public string Filename { get; set; }
    public string RecognitionStatus { get; set; }
    public List<Phrase> Phrases { get; set; }
    
    public override string ToString()
    {
        return JsonConvert.SerializeObject(this);
    }
}
