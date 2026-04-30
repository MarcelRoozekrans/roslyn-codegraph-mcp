using System;

namespace TestLib;

public class EventPublisher
{
    public event EventHandler? Clicked;
    public event EventHandler? Clicked2;
    public void Raise() => Clicked?.Invoke(this, EventArgs.Empty);
}

public interface IBusEventPublisher
{
    event EventHandler? MessageReceived;
}

public class BusA : IBusEventPublisher
{
    public event EventHandler? MessageReceived;
}

public class BusB : IBusEventPublisher
{
    public event EventHandler? MessageReceived;
}

public class EventSubscriberSamples
{
    private readonly EventPublisher _publisher = new();
    private readonly BusA _busA = new();
    private readonly BusB _busB = new();

    public void SubscribeMethodGroup()
    {
        _publisher.Clicked += OnClicked;
    }

    public void UnsubscribeMethodGroup()
    {
        _publisher.Clicked -= OnClicked;
    }

    public void SubscribeLambda()
    {
        _publisher.Clicked += (s, e) => { };
    }

    public void SubscribeAnonymousMethod()
    {
        _publisher.Clicked += delegate (object? s, EventArgs e) { };
    }

    public void SubscribeBothBuses()
    {
        _busA.MessageReceived += OnBusMessage;
        _busB.MessageReceived += OnBusMessage;
    }

    public void TwoSubscriptionsOnSameLine()
    {
        _publisher.Clicked += OnClicked; _publisher.Clicked2 += OnClicked;
    }

    private void OnClicked(object? sender, EventArgs e) { }
    private void OnBusMessage(object? sender, EventArgs e) { }
}
