using CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class ObtenerFirmaGuardadaUsuarioQueryTests
{
    [Fact]
    public async Task Devuelve_null_cuando_no_se_puede_identificar_al_usuario_actual()
    {
        var contexto = new DocumentosQueryContextFalso();
        var handler = new ObtenerFirmaGuardadaUsuarioQueryHandler(contexto, new CurrentUserServiceFalso(null));

        var resultado = await handler.Handle(new ObtenerFirmaGuardadaUsuarioQuery(), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Devuelve_null_cuando_el_usuario_no_tiene_firma_guardada()
    {
        var usuarioId = Guid.NewGuid();
        var contexto = new DocumentosQueryContextFalso();
        var handler = new ObtenerFirmaGuardadaUsuarioQueryHandler(contexto, new CurrentUserServiceFalso(usuarioId));

        var resultado = await handler.Handle(new ObtenerFirmaGuardadaUsuarioQuery(), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Devuelve_la_firma_guardada_del_usuario_actual_y_no_la_de_otro()
    {
        var usuarioId = Guid.NewGuid();
        var contexto = new DocumentosQueryContextFalso();
        contexto.ListaFirmasGuardadasUsuario.Add(new FirmaGuardadaUsuario(usuarioId, "url/mia.png", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        contexto.ListaFirmasGuardadasUsuario.Add(new FirmaGuardadaUsuario(Guid.NewGuid(), "url/ajena.png", DateTime.UtcNow));
        var handler = new ObtenerFirmaGuardadaUsuarioQueryHandler(contexto, new CurrentUserServiceFalso(usuarioId));

        var resultado = await handler.Handle(new ObtenerFirmaGuardadaUsuarioQuery(), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.ImagenUrl.Should().Be("url/mia.png");
    }
}
