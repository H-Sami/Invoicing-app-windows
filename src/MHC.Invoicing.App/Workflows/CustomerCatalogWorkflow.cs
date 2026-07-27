namespace MHC.Invoicing.App.Workflows;

internal enum WorkflowErrorKind
{
    None,
    Validation,
    Conflict,
    Unexpected,
}

internal sealed record CustomerCatalogWorkflowState<T>(
    IReadOnlyList<T> Items,
    string Query,
    bool IsBusy,
    WorkflowErrorKind ErrorKind,
    string? ErrorMessage)
{
    internal static CustomerCatalogWorkflowState<T> Empty { get; } =
        new(Array.Empty<T>(), string.Empty, false, WorkflowErrorKind.None, null);
}

internal sealed class CustomerCatalogWorkflow<T>
{
    private readonly Func<Exception, WorkflowErrorKind> _classifyError;
    private CancellationTokenSource? _operationCancellation;
    private long _operationId;

    internal CustomerCatalogWorkflow(Func<Exception, WorkflowErrorKind> classifyError)
    {
        ArgumentNullException.ThrowIfNull(classifyError);
        _classifyError = classifyError;
    }

    internal event EventHandler? StateChanged;

    internal CustomerCatalogWorkflowState<T> State { get; private set; } =
        CustomerCatalogWorkflowState<T>.Empty;

    internal async Task LoadAsync(
        string? query,
        Func<string, CancellationToken, Task<IReadOnlyList<T>>> load,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(load);
        string normalizedQuery = query?.Trim() ?? string.Empty;
        (long operationId, CancellationToken token) = BeginOperation(normalizedQuery, cancellationToken);
        try
        {
            IReadOnlyList<T> items = await load(normalizedQuery, token).ConfigureAwait(true);
            if (IsCurrent(operationId))
            {
                Publish(new(items, normalizedQuery, false, WorkflowErrorKind.None, null));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            CompleteCancellation(operationId);
        }
        catch (Exception exception)
        {
            CompleteError(operationId, exception);
        }
    }

    internal async Task<bool> MutateAsync(
        Func<CancellationToken, Task> mutate,
        Func<string, CancellationToken, Task<IReadOnlyList<T>>> reload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(reload);
        string query = State.Query;
        (long operationId, CancellationToken token) = BeginOperation(query, cancellationToken);
        try
        {
            await mutate(token).ConfigureAwait(true);
            IReadOnlyList<T> items = await reload(query, token).ConfigureAwait(true);
            if (!IsCurrent(operationId))
            {
                return false;
            }

            Publish(new(items, query, false, WorkflowErrorKind.None, null));
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            CompleteCancellation(operationId);
            return false;
        }
        catch (Exception exception)
        {
            CompleteError(operationId, exception);
            return false;
        }
    }

    internal void Cancel()
    {
        _operationCancellation?.Cancel();
        checked
        {
            _operationId++;
        }

        Publish(State with { IsBusy = false, ErrorKind = WorkflowErrorKind.None, ErrorMessage = null });
    }

    private (long OperationId, CancellationToken Token) BeginOperation(
        string query,
        CancellationToken cancellationToken)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long operationId = checked(++_operationId);
        Publish(State with
        {
            Query = query,
            IsBusy = true,
            ErrorKind = WorkflowErrorKind.None,
            ErrorMessage = null,
        });
        return (operationId, _operationCancellation.Token);
    }

    private bool IsCurrent(long operationId) => operationId == _operationId;

    private void CompleteCancellation(long operationId)
    {
        if (IsCurrent(operationId))
        {
            Publish(State with { IsBusy = false, ErrorKind = WorkflowErrorKind.None, ErrorMessage = null });
        }
    }

    private void CompleteError(long operationId, Exception exception)
    {
        if (IsCurrent(operationId))
        {
            Publish(State with
            {
                IsBusy = false,
                ErrorKind = _classifyError(exception),
                ErrorMessage = null,
            });
        }
    }

    private void Publish(CustomerCatalogWorkflowState<T> state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
