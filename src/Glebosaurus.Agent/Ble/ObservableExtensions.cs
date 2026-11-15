namespace Glebosaurus.Agent.Ble;

internal static class ObservableExtensions
{
    /// <summary>Subskrypcja z delegatów, bez zależności od System.Reactive.</summary>
    public static IDisposable Observe<T>(this IObservable<T> source, Action<T> onNext, Action<Exception>? onError = null) =>
        source.Subscribe(new ActionObserver<T>(onNext, onError));

    private sealed class ActionObserver<T>(Action<T> onNext, Action<Exception>? onError) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) => onError?.Invoke(error);
        public void OnCompleted() { }
    }
}
