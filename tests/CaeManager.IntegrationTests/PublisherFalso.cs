using MediatR;

namespace CaeManager.IntegrationTests;

/// <summary>
/// <see cref="IPublisher"/> que recoge lo publicado sin despachar a ningún
/// handler — mismo papel que <c>AlcanceDatosServiceFalso</c>/<c>CurrentUserServiceFalso</c>.
/// Los tests que solo construyen un handler a mano para ejercitar su lógica no
/// quieren arrastrar los efectos de los suscriptores reales.
/// </summary>
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
