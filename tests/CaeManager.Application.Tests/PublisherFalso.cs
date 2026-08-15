using MediatR;

namespace CaeManager.Application.Tests;

/// <summary><see cref="IPublisher"/> que recoge lo publicado sin despachar a ningún handler.</summary>
public class PublisherFalso : IPublisher
{
    public List<INotification> Publicados { get; } = [];

    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        if (notification is INotification tipada) Publicados.Add(tipada);
        return Task.CompletedTask;
    }

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        Publicados.Add(notification);
        return Task.CompletedTask;
    }
}
