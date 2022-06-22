using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapGet("/", () => "Hello World!");
app.MapGet("/test", async () =>
{
    using var eventListener = new HttpEventListener();
    var client = new HttpClient();
    return await client.GetFromJsonAsync<Todo>("https://jsonplaceholder.typicode.com/todos/1");
})
.WithName("Test");
app.MapGet("/test2", async () =>
{
    using var observer = new HttpRequestsObserver();
    using (DiagnosticListener.AllListeners.Subscribe(observer))
    {
        var client = new HttpClient();
        return await client.GetFromJsonAsync<Todo>("https://jsonplaceholder.typicode.com/todos/1");
    }
})
.WithName("Test2");

app.Run();


record Todo
{
    public int UserId { set; get; }
    public string Title { set; get; }
    public bool Completed { get; set; }

}

internal sealed class HttpRequestsObserver : IDisposable, IObserver<DiagnosticListener>
{
    private IDisposable _subscription;

    public void OnNext(DiagnosticListener value)
    {
        if (value.Name == "HttpHandlerDiagnosticListener")
        {
            _subscription = value.Subscribe(new HttpHandlerDiagnosticListener());
        }
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }

    public void Dispose()
    {
        _subscription?.Dispose();
    }

    private sealed class HttpHandlerDiagnosticListener : IObserver<KeyValuePair<string, object>>
    {
        private static readonly Func<object, HttpRequestMessage> RequestAccessor = CreateGetRequest();
        private static readonly Func<object, HttpResponseMessage> ResponseAccessor = CreateGetResponse();

        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public void OnNext(KeyValuePair<string, object> value)
        {
            // note: Legacy applications can use "System.Net.Http.HttpRequest" and "System.Net.Http.Response"
            if (value.Key == "System.Net.Http.HttpRequestOut.Start")
            {
                // The type is private, so we need to use reflection to access it.
                var request = RequestAccessor(value.Value);
                Console.WriteLine($"{request.Method} {request.RequestUri} {request.Version} (UserAgent: {request.Headers.UserAgent})");
            }
            else if (value.Key == "System.Net.Http.HttpRequestOut.Stop")
            {
                // The type is private, so we need to use reflection to access it.
                var response = ResponseAccessor(value.Value);
                Console.WriteLine($"{response.StatusCode} {response.RequestMessage.RequestUri}");
            }
        }

        private static Func<object, HttpRequestMessage> CreateGetRequest()
        {
            var requestDataType = Type.GetType("System.Net.Http.DiagnosticsHandler+ActivityStartData, System.Net.Http", throwOnError: true);
            var requestProperty = requestDataType.GetProperty("Request");
            return (object o) => (HttpRequestMessage)requestProperty.GetValue(o);
        }

        private static Func<object, HttpResponseMessage> CreateGetResponse()
        {
            var requestDataType = Type.GetType("System.Net.Http.DiagnosticsHandler+ActivityStopData, System.Net.Http", throwOnError: true);
            var requestProperty = requestDataType.GetProperty("Response");
            return (object o) => (HttpResponseMessage)requestProperty.GetValue(o);
        }
    }
}


sealed class HttpEventListener : EventListener
{
    private readonly AsyncLocal<Request> _currentRequest = new();
    private sealed record Request(string Url, Stopwatch ExecutionTime);
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        switch (eventSource.Name)
        {
            case "System.Net.Http":
                EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All);
                break;

            // Enable EventWrittenEventArgs.ActivityId to correlate Start and Stop events
            case "System.Threading.Tasks.TplEventSource":
                const EventKeywords TasksFlowActivityIds = (EventKeywords)0x80;
                EnableEvents(eventSource, EventLevel.LogAlways, TasksFlowActivityIds);
                break;
        }

        base.OnEventSourceCreated(eventSource);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        // note: Use eventData.ActivityId to correlate Start and Stop events
        if (eventData.EventId == 1) // eventData.EventName == "RequestStart"
        {
            var scheme = (string)eventData.Payload[0];
            var host = (string)eventData.Payload[1];
            var port = (int)eventData.Payload[2];
            var pathAndQuery = (string)eventData.Payload[3];
            var versionMajor = (byte)eventData.Payload[4];
            var versionMinor = (byte)eventData.Payload[5];
            var policy = (HttpVersionPolicy)eventData.Payload[6];
            _currentRequest.Value = new Request($"{scheme}://{host}:{port}{pathAndQuery}", Stopwatch.StartNew());
            Console.WriteLine($"{eventData.ActivityId} {eventData.EventName} {scheme}://{host}:{port}{pathAndQuery} HTTP/{versionMajor}.{versionMinor}");
        }
        else if (eventData.EventId == 2) // eventData.EventName == "RequestStop"
        {
            var currentRequest = _currentRequest.Value;
            if (currentRequest != null)
            {
                Console.WriteLine($"{currentRequest.Url} executed in {currentRequest.ExecutionTime.ElapsedMilliseconds:F1}ms");
            }
            Console.WriteLine(eventData.ActivityId + " " + eventData.EventName);
        }
    }
}