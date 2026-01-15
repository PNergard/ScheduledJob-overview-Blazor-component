using EPiServer.DataAbstraction;
using EPiServer.Scheduler;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using MudBlazor;
using Microsoft.JSInterop;
using System.Text;

namespace Nergard.ScheduledJobsMonitor.Components;

public partial class ScheduledJobsMonitor
{
    [Inject]
    private IScheduledJobRepository JobRepository { get; set; } = default!;

    [Inject]
    private IScheduledJobExecutor JobExecutor { get; set; } = default!;

    [Inject]
    private IScheduledJobLogRepository JobLogRepository { get; set; } = default!;

    [Inject]
    private IOptions<SchedulerOptions> SchedulerOptions { get; set; } = default!;

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    [CascadingParameter(Name = "ShowToolHeaders")]
    public bool ShowHeader { get; set; } = true;

    private List<JobViewModel> _jobs = new();
    private List<JobViewModel> _customJobs = new();
    private List<JobViewModel> _builtInJobs = new();
    private JobViewModel? _selectedJob;
    private List<ScheduledJobLogItem> _allMessages = new();
    private ScheduledJobLogItem? _selectedMessage;

    private bool _isLoading = false;
    private bool _isLoadingHistory = false;
    private bool _isExecuting = false;
    private bool _messageDetailOpen = false;
    private bool _checkLastStatus = false;

    private string _statusMessage = string.Empty;
    private Severity _messageSeverity = Severity.Success;
    private string _filterText = string.Empty;
    private string _messageFilterText = string.Empty;
    private string _messageHighlightText = string.Empty;

    private const int MaxMessagesToRetrieve = 1000;

    // Helper method to determine if a log entry was successful
    private bool IsLogEntrySuccessful(ScheduledJobLogItem logItem)
    {
        // EPiServer ScheduledJobLogItem doesn't have a direct Success/Succeeded property
        // We check if there's no exception information in the message
        // Typically, failed jobs contain "Exception" or "Error" keywords
        if (string.IsNullOrEmpty(logItem.Message))
            return true;

        var message = logItem.Message.ToLower();
        return !message.Contains("exception") &&
               !message.Contains("error:") &&
               !message.Contains("failed");
    }

    protected override async Task OnInitializedAsync()
    {
        if (!SchedulerOptions.Value.Enabled)
        {
            _statusMessage = "Scheduler is currently disabled in development mode. Jobs can still be executed manually, but automatic scheduling is inactive. To enable: Remove the 'options.Enabled = false' line in Startup.cs.";
            _messageSeverity = Severity.Warning;
        }

        await RefreshJobs();
    }

    private async Task RefreshJobs()
    {
        _isLoading = true;
        if (_statusMessage.Contains("disabled"))
        {
            // Keep scheduler warning message
        }
        else
        {
            _statusMessage = string.Empty;
        }
        StateHasChanged();

        try
        {
            var jobs = JobRepository.List();
            _jobs = jobs.Select(j => new JobViewModel
            {
                Id = j.ID,
                Name = j.Name,
                IsEnabled = j.IsEnabled,
                IsRunning = j.IsRunning,
                LastExecution = j.LastExecution,
                NextExecution = j.NextExecution,
                HasLastExecutionFailed = false,
                LastDuration = null,
                ScheduledJob = j
            })
            .OrderBy(j => j.Name)
            .ToList();

            // Categorize jobs into custom and built-in
            _customJobs = _jobs.Where(j => !IsBuiltInJob(j.ScheduledJob)).ToList();
            _builtInJobs = _jobs.Where(j => IsBuiltInJob(j.ScheduledJob)).ToList();

            // Load execution status if toggle is enabled
            if (_checkLastStatus)
            {
                await LoadLastExecutionStatusesAsync(_jobs);
            }
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error loading jobs: {ex.Message}";
            _messageSeverity = Severity.Error;
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }

        await Task.CompletedTask;
    }

    private async Task ExecuteJob(JobViewModel job)
    {
        // Auto-select the job to show execution details
        _selectedJob = job;
        await LoadExecutionHistory(job);

        _isExecuting = true;
        _statusMessage = $"Executing job '{job.Name}'...";
        _messageSeverity = Severity.Info;
        StateHasChanged();

        try
        {
            await JobExecutor.StartAsync(job.ScheduledJob);

            _statusMessage = $"Job '{job.Name}' started successfully.";
            _messageSeverity = Severity.Success;

            await Task.Delay(1000);

            await RefreshJobs();

            // Refresh execution history for the selected job
            if (_selectedJob?.Id == job.Id)
            {
                await LoadExecutionHistory(job);
            }
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error executing job '{job.Name}': {ex.Message}";
            _messageSeverity = Severity.Error;
        }
        finally
        {
            _isExecuting = false;
            StateHasChanged();
        }
    }

    private async Task OnJobSelected(JobViewModel? job)
    {
        _selectedJob = job;

        if (job != null)
        {
            await LoadExecutionHistory(job);
        }
    }

    private async Task LoadExecutionHistory(JobViewModel job)
    {
        _isLoadingHistory = true;
        _allMessages.Clear();
        StateHasChanged();

        try
        {
            var result = await JobLogRepository.GetAsync(job.Id, 1, MaxMessagesToRetrieve);

            if (result?.PagedResult != null)
            {
                _allMessages = result.PagedResult.ToList();
            }
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error loading execution history: {ex.Message}";
            _messageSeverity = Severity.Error;
        }
        finally
        {
            _isLoadingHistory = false;
            StateHasChanged();
        }
    }

    private async Task LoadLastExecutionStatusesAsync(List<JobViewModel> jobs)
    {
        foreach (var job in jobs)
        {
            try
            {
                // Get only the most recent log entry (page 1, size 1)
                var result = await JobLogRepository.GetAsync(job.Id, 1, 1);

                if (result?.PagedResult != null && result.PagedResult.Any())
                {
                    var lastLog = result.PagedResult.First();
                    // Use existing helper to check if it failed
                    job.HasLastExecutionFailed = !IsLogEntrySuccessful(lastLog);
                }
                else
                {
                    // No history means never run, not a failure
                    job.HasLastExecutionFailed = false;
                }
            }
            catch (Exception)
            {
                // If we can't load history, assume not failed
                job.HasLastExecutionFailed = false;
            }
        }
    }

    private ScheduledJobLogItem? GetLatestLogEntry()
    {
        return _allMessages.FirstOrDefault();
    }

    private List<ScheduledJobLogItem> GetFilteredMessages()
    {
        if (string.IsNullOrWhiteSpace(_messageFilterText))
            return _allMessages;

        return _allMessages
            .Where(m => m.Message.Contains(_messageFilterText, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private string GetMessageSnippet(string message, int maxLength = 100)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;

        if (message.Length <= maxLength)
            return message;

        return message.Substring(0, maxLength) + "...";
    }

    private void OpenMessageDetail(ScheduledJobLogItem message)
    {
        _selectedMessage = message;
        _messageDetailOpen = true;
        _messageHighlightText = string.Empty;
    }

    private void CloseMessageDetail()
    {
        _messageDetailOpen = false;
        _selectedMessage = null;
        _messageHighlightText = string.Empty;
    }

    private string HighlightKeywords(string text)
    {
        if (string.IsNullOrWhiteSpace(_messageHighlightText) || string.IsNullOrEmpty(text))
            return text;

        // Simple highlighting - wrap matches in <mark> tags
        var keywords = _messageHighlightText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = text;

        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                continue;

            var pattern = System.Text.RegularExpressions.Regex.Escape(keyword);
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                pattern,
                $"<mark style='background-color: yellow; font-weight: bold;'>{keyword}</mark>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        return result;
    }

    private async Task ExportMessages()
    {
        try
        {
            var delimiter = "⚡⚡⚡"; // Very unlikely to appear in log messages
            var sb = new StringBuilder();

            // Header
            sb.AppendLine($"ExecutedUtc{delimiter}Status{delimiter}Message");

            // Data rows
            foreach (var message in GetFilteredMessages())
            {
                var executedUtc = message.CompletedUtc.ToString("yyyy-MM-dd HH:mm:ss");
                var status = IsLogEntrySuccessful(message) ? "Success" : "Failed";
                var messageText = message.Message.Replace(delimiter, " "); // Remove delimiter if it exists in message

                sb.AppendLine($"{executedUtc}{delimiter}{status}{delimiter}{messageText}");
            }

            var content = sb.ToString();
            var fileName = $"ScheduledJobLog_{_selectedJob?.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";

            // Use JS Interop to trigger download
            await JSRuntime.InvokeVoidAsync("downloadFile", fileName, content);
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error exporting messages: {ex.Message}";
            _messageSeverity = Severity.Error;
            StateHasChanged();
        }
    }

    private void ClearStatusMessage()
    {
        _statusMessage = string.Empty;
        StateHasChanged();
    }

    private bool IsBuiltInJob(ScheduledJob job)
    {
        if (string.IsNullOrEmpty(job.TypeName))
            return false;

        return job.TypeName.StartsWith("EPiServer.", StringComparison.OrdinalIgnoreCase) ||
               job.TypeName.StartsWith("Optimizely.", StringComparison.OrdinalIgnoreCase);
    }

    private List<JobViewModel> FilterJobs(List<JobViewModel> jobs)
    {
        if (string.IsNullOrWhiteSpace(_filterText))
            return jobs;

        return jobs.Where(j => j.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
                   .ToList();
    }

    private List<JobViewModel> FilteredCustomJobs => FilterJobs(_customJobs);
    private List<JobViewModel> FilteredBuiltInJobs => FilterJobs(_builtInJobs);

    public class JobViewModel
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public bool IsRunning { get; set; }
        public DateTime? LastExecution { get; set; }
        public DateTime? NextExecution { get; set; }
        public bool HasLastExecutionFailed { get; set; }
        public TimeSpan? LastDuration { get; set; }
        public ScheduledJob ScheduledJob { get; set; } = default!;
    }
}
