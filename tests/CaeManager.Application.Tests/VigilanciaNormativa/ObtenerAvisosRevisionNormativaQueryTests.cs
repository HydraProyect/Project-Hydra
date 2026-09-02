using CaeManager.Application.VigilanciaNormativa.Queries.ObtenerAvisosRevisionNormativa;
using CaeManager.Domain.VigilanciaNormativa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.VigilanciaNormativa;

/// <summary>
/// DEC-8: cualquier usuario autenticado ve el catálogo completo — sin
/// distinción de rol ni de tenant, porque el aviso no es de ningún tenant.
/// </summary>
public class ObtenerAvisosRevisionNormativaQueryTests
{
    private static AvisoRevisionNormativa CrearAviso(string identificador, DateOnly fecha) => new(
        identificador, fecha, $"Publicación {identificador}", $"https://boe.es/{identificador}", "LPRL", DateTime.UtcNow);

    [Fact]
    public async Task Sin_usuario_identificado_no_devuelve_nada()
    {
        var contexto = new VigilanciaNormativaQueryContextFalso();
        contexto.ListaAvisos.Add(CrearAviso("BOE-1", new DateOnly(2026, 1, 1)));

        var handler = new ObtenerAvisosRevisionNormativaQueryHandler(contexto, new CurrentUserServiceFalso());

        var resultado = await handler.Handle(new ObtenerAvisosRevisionNormativaQuery(), CancellationToken.None);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Cualquier_usuario_autenticado_ve_el_catalogo_completo_sin_filtrar_por_rol()
    {
        var contexto = new VigilanciaNormativaQueryContextFalso();
        contexto.ListaAvisos.Add(CrearAviso("BOE-1", new DateOnly(2026, 1, 1)));
        contexto.ListaAvisos.Add(CrearAviso("BOE-2", new DateOnly(2026, 6, 1)));

        // Rol "Gestor CAE de un cliente" cualquiera: la lectura no distingue.
        var handler = new ObtenerAvisosRevisionNormativaQueryHandler(
            contexto, new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae", Guid.NewGuid()));

        var resultado = await handler.Handle(new ObtenerAvisosRevisionNormativaQuery(), CancellationToken.None);

        resultado.Should().HaveCount(2);
    }

    [Fact]
    public async Task Ordena_por_fecha_de_publicacion_descendente()
    {
        var contexto = new VigilanciaNormativaQueryContextFalso();
        contexto.ListaAvisos.Add(CrearAviso("BOE-antiguo", new DateOnly(2026, 1, 1)));
        contexto.ListaAvisos.Add(CrearAviso("BOE-reciente", new DateOnly(2026, 8, 1)));

        var handler = new ObtenerAvisosRevisionNormativaQueryHandler(
            contexto, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", Guid.NewGuid()));

        var resultado = await handler.Handle(new ObtenerAvisosRevisionNormativaQuery(), CancellationToken.None);

        resultado[0].IdentificadorBoe.Should().Be("BOE-reciente");
        resultado[1].IdentificadorBoe.Should().Be("BOE-antiguo");
    }
}
