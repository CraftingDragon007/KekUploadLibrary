﻿using Newtonsoft.Json;

namespace KekUploadLibrary;

public class UploadClient
{
    private string _apiBaseUrl;
    private long _chunkSize;
    
    public UploadClient(string apiBaseUrl, long chunkSize)
    {
        _apiBaseUrl = apiBaseUrl;
        _chunkSize = chunkSize;
    }

    public UploadClient(string apiBaseUrl)
    {
        _apiBaseUrl = apiBaseUrl;
        _chunkSize = 1024 * 1024 * 2;
    }

    public event UploadStreamCreateEventHandler? UploadStreamCreateEvent;
    public event UploadChunkCompleteEventHandler? UploadChunkCompleteEvent;
    public event UploadCompleteEventHandler? UploadCompleteEvent;
    public event UploadErrorEventHandler? UploadErrorEvent;

    public delegate void UploadStreamCreateEventHandler(object sender, UploadStreamCreateEventArgs args);
    public delegate void UploadChunkCompleteEventHandler(object sender, UploadChunkCompleteEventArgs args);
    public delegate void UploadCompleteEventHandler(object sender, UploadCompleteEventArgs args);
    public delegate void UploadErrorEventHandler(object sender, UploadErrorEventArgs args);

    protected virtual void OnUploadStreamCreateEvent(UploadStreamCreateEventArgs e)
    {
        UploadStreamCreateEvent?.Invoke(this, e);
    }

    protected virtual void OnUploadChunkCompleteEvent(UploadChunkCompleteEventArgs e)
    {
        UploadChunkCompleteEvent?.Invoke(this, e);
    }
    
    protected virtual void OnUploadCompleteEvent(UploadCompleteEventArgs e)
    {
        UploadCompleteEvent?.Invoke(this, e);
    }
    
    protected virtual void OnUploadErrorEvent(UploadErrorEventArgs e)
    {
        UploadErrorEvent?.Invoke(this, e);
    }

    public string UploadFile(string path)
    {
        var file = Path.GetFullPath(path);
        if(!File.Exists(file))
        {
            throw new KekException("The provided file does not exist!", new FileNotFoundException("The provided file does not exist!", file));
        }
        var fileInfo = new FileInfo(file);
        var client = new HttpClient();
        
        var request = new HttpRequestMessage() {
            RequestUri = new Uri(_apiBaseUrl + "/c/" + fileInfo.Extension[1..]),
            Method = HttpMethod.Post
        };
        HttpResponseMessage? responseMessage;
        try
        {
            responseMessage = client.Send(request);
            responseMessage.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException e)
        {
            throw new KekException("Could not create upload-stream!", e);
        }

        var uploadStreamId = Utils.ParseUploadStreamId(new StreamReader(responseMessage.Content.ReadAsStream()).ReadToEnd());
        if(uploadStreamId == null)
            throw new KekException("Could not create upload-stream!");
        
        OnUploadStreamCreateEvent(new UploadStreamCreateEventArgs(uploadStreamId));

        var stream = File.OpenRead(file);

        var fileSize = fileInfo.Length;
        var maxChunkSize = _chunkSize; //1024 * _chunkSize;
        var chunks = (int)Math.Ceiling(fileSize/(double)maxChunkSize);
        
        for(int chunk = 0; chunk < chunks; chunk++) {
            var chunkSize = Math.Min(stream.Length-chunk*maxChunkSize, maxChunkSize);
            byte[] buf = new byte[chunkSize];

            int readBytes = 0;
            while(readBytes < chunkSize) readBytes += stream.Read(buf, readBytes, (int)Math.Min(stream.Length-(readBytes+chunk*chunkSize), chunkSize));
                var hash = Utils.HashBytes(buf);
                // index is the number of bytes in the chunk
                var uploadRequest = new HttpRequestMessage {
                    RequestUri = new Uri(_apiBaseUrl + "/u/" + uploadStreamId + "/" + hash),
                    Method = HttpMethod.Post,
                    Content = new ByteArrayContent(buf)
                };
                HttpResponseMessage? responseMsg = null;
                responseMessage = null;
                try
                {
                    responseMsg = client.Send(uploadRequest);
                    responseMsg.EnsureSuccessStatusCode();
                }
                catch (HttpRequestException e)
                {
                    OnUploadErrorEvent(new UploadErrorEventArgs(e, RequestErrorResponse.ParseErrorResponse(responseMsg)));
                    var success = false;
                    while (!success)
                    {
                        try
                        {
                            responseMessage = client.Send(uploadRequest);
                            responseMessage.EnsureSuccessStatusCode();
                            success = true;
                        }
                        catch (HttpRequestException ex)
                        {
                            OnUploadErrorEvent(new UploadErrorEventArgs(ex, RequestErrorResponse.ParseErrorResponse(responseMessage)));
                            Thread.Sleep(500);
                        }
                    }
                }
                OnUploadChunkCompleteEvent(new UploadChunkCompleteEventArgs(hash, chunk, chunks));
        }
        var fileHash = Utils.HashFile(file);
        Console.WriteLine(fileHash);

        var finishRequest = new HttpRequestMessage {   
            RequestUri = new Uri(_apiBaseUrl + "/f/" + uploadStreamId + "/" + fileHash),
            Method = HttpMethod.Post
        };

        HttpResponseMessage? finishResponse = null;
        try
        {
            finishResponse = client.Send(finishRequest);
            finishResponse.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException e)
        {
            throw new KekException("Failed to send finish request!", e, RequestErrorResponse.ParseErrorResponse(finishResponse));
        }
        var downloadId = finishResponse.Content.ReadAsStringAsync().Result;
        var url = _apiBaseUrl + "/d/" + Utils.ParseDownloadId(downloadId);
        if(url == null) throw new KekException("Failed to parse download url!");
        OnUploadCompleteEvent(new UploadCompleteEventArgs(path, url));
        return url;
    }

    public string UploadBytes(byte[] data, string extension)
    {
        var client = new HttpClient();
        
        var request = new HttpRequestMessage() {
            RequestUri = new Uri(_apiBaseUrl + "/c/" + extension),
            Method = HttpMethod.Post
        };
        
        HttpResponseMessage? responseMessage = null;
        try
        {
            responseMessage = client.Send(request);
            responseMessage.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException e)
        {
            throw new KekException("Could not create upload-stream!", e, RequestErrorResponse.ParseErrorResponse(responseMessage));
        }

        var uploadStreamId = Utils.ParseUploadStreamId(new StreamReader(responseMessage.Content.ReadAsStream()).ReadToEnd());
        
        if(uploadStreamId == null)
            throw new KekException("Could not parse upload-stream id!");
        
        OnUploadStreamCreateEvent(new UploadStreamCreateEventArgs(uploadStreamId));

        var stream = new MemoryStream(data);

        var fileSize = data.Length;
        var maxChunkSize = 1024 * _chunkSize;
        var chunks = (int)Math.Ceiling(fileSize/(double)maxChunkSize);

        for(int chunk = 0; chunk < chunks; chunk++) {
            var chunkSize = Math.Min(stream.Length-chunk*maxChunkSize, maxChunkSize);
            byte[] buf = new byte[chunkSize];
            int readBytes = 0;
            while(readBytes < chunkSize) readBytes += stream.Read(buf, readBytes, (int)Math.Min(stream.Length-(readBytes+chunk*chunkSize), chunkSize));

            var hash = Utils.HashBytes(buf);

            // index is the number of bytes in the chunk
            var uploadRequest = new HttpRequestMessage {
                 RequestUri = new Uri(_apiBaseUrl + "/u/" + uploadStreamId + "/" + hash),
                 Method = HttpMethod.Post,
                 Content = new ByteArrayContent(buf)
            };

            HttpResponseMessage? uploadResponse = null;
            
            try
            {
                uploadResponse = client.Send(uploadRequest);
                uploadResponse.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException e)
            {
                OnUploadErrorEvent(new UploadErrorEventArgs(e, RequestErrorResponse.ParseErrorResponse(uploadResponse)));
                var success = false;
                while (!success)
                {
                    try
                    {
                        responseMessage = client.Send(uploadRequest);
                        responseMessage.EnsureSuccessStatusCode();
                        success = true;
                    }
                    catch (HttpRequestException ex)
                    {
                        OnUploadErrorEvent(new UploadErrorEventArgs(ex, RequestErrorResponse.ParseErrorResponse(responseMessage)));
                        Thread.Sleep(500);
                    }
                }
            }
            OnUploadChunkCompleteEvent(new UploadChunkCompleteEventArgs(hash, chunk, chunks));
        }
            
        var fileHash = Utils.HashBytes(data);

        var finishRequest = new HttpRequestMessage {   
            RequestUri = new Uri(_apiBaseUrl + "/f/" + uploadStreamId + "/" + fileHash),
            Method = HttpMethod.Post
        };

        HttpResponseMessage? finishResponse = null;
        try
        {
            finishResponse = client.Send(finishRequest);
            finishResponse.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException e)
        {
            throw new KekException("Failed to send finish request!", e, RequestErrorResponse.ParseErrorResponse(finishResponse));
        }

        var downloadId = finishResponse.Content.ReadAsStringAsync().Result;
        var url = _apiBaseUrl + "/d/" + Utils.ParseDownloadId(downloadId);
        if(url == null) throw new KekException("Failed to parse download url!");
        OnUploadCompleteEvent(new UploadCompleteEventArgs(null, url));
        return url;
    }

    public string UploadStream(Stream stream, string extension)
    {
        var client = new HttpClient();
        
        var request = new HttpRequestMessage() {
            RequestUri = new Uri(_apiBaseUrl + "/c/" + extension),
            Method = HttpMethod.Post
        };
        
        HttpResponseMessage? responseMessage = null;
        try
        {
            responseMessage = client.Send(request);
            responseMessage.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException e)
        {
            throw new KekException("Could not create upload-stream!", e, RequestErrorResponse.ParseErrorResponse(responseMessage));
        }

        var uploadStreamId = Utils.ParseUploadStreamId(new StreamReader(responseMessage.Content.ReadAsStream()).ReadToEnd());
        if(uploadStreamId == null) throw new KekException("Could not parse upload-stream-id!");
            
        OnUploadStreamCreateEvent(new UploadStreamCreateEventArgs(uploadStreamId));
        
        var fileSize = stream.Length;
        var maxChunkSize = 1024 * _chunkSize;
        var chunks = (int)Math.Ceiling(fileSize/(double)maxChunkSize);

        for(int chunk = 0; chunk < chunks; chunk++) {
            var chunkSize = Math.Min(stream.Length-chunk*maxChunkSize, maxChunkSize);
            byte[] buf = new byte[chunkSize];
            int readBytes = 0;
            while(readBytes < chunkSize) readBytes += stream.Read(buf, readBytes, (int)Math.Min(stream.Length-(readBytes+chunk*chunkSize), chunkSize));

            var hash = Utils.HashBytes(buf);

            // index is the number of bytes in the chunk
            var uploadRequest = new HttpRequestMessage {
                RequestUri = new Uri(_apiBaseUrl + "/u/" + uploadStreamId + "/" + hash),
                Method = HttpMethod.Post,
                Content = new ByteArrayContent(buf)
            };

            HttpResponseMessage? responseMsg = null;
            try
            {
                responseMsg = client.Send(uploadRequest);
                responseMsg.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException e)
            {
                OnUploadErrorEvent(new UploadErrorEventArgs(e, RequestErrorResponse.ParseErrorResponse(responseMsg)));
                var success = false;
                while (!success)
                {
                    try
                    {
                        responseMessage = client.Send(uploadRequest);
                        responseMessage.EnsureSuccessStatusCode();
                        success = true;
                    }
                    catch (HttpRequestException ex)
                    {
                        OnUploadErrorEvent(new UploadErrorEventArgs(ex, RequestErrorResponse.ParseErrorResponse(responseMessage)));
                        Thread.Sleep(500);
                    }
                }
            }
            OnUploadChunkCompleteEvent(new UploadChunkCompleteEventArgs(hash, chunk, chunks));
        }
            
        var fileHash = Utils.HashStream(stream);
        

        var finishRequest = new HttpRequestMessage {   
            RequestUri = new Uri(_apiBaseUrl + "/f/" + uploadStreamId + "/" + fileHash),
            Method = HttpMethod.Post
        };

        HttpResponseMessage? finishResponse = null;
        try
        {
            finishResponse = client.Send(finishRequest);
            finishResponse.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException e)
        {
            throw new KekException("Failed to send finish request!", e, RequestErrorResponse.ParseErrorResponse(finishResponse));
        }

        var downloadId = finishResponse.Content.ReadAsStringAsync().Result;
        return _apiBaseUrl + "/d/" + downloadId;
    }
}