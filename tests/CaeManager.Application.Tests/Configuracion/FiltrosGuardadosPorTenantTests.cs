using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Configuracion;

/// <summary>
/// Los filtros guardados son de un usuario DENTRO de un Tenant. El caso que
/// los rompía: un Gestor CAE de un Operador CAE externo, con un solo usuario y
/// cartera en varios Tenant, veía en cada Tenant los filtros guardados en los
/// demás — con identificadores (de Empresa, de Centro…) ajenos dentro.
///
/// La tabla no lleva Tenant propio ni RLS: la frontera es la clave compuesta
/// que Application guarda en <see cref="FiltroGuardado.Pantalla"/>. Estos tests
/// ejercitan los tres handlers sobre el MISMO almacén, que es donde se vería
/// que la lectura y la escritura componen la clave de forma distinta.
/// </summary>
public class FiltrosGuardadosPorTenantTests
{
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly FiltroGuardadoRepositorioFalso _almacen = new();

    private Task<Result<Guid>> GuardarAsync(Guid? tenantId, string pantalla, string nombre, Guid? usuarioId = null) =>
        new GuardarFiltroCommandHandler(
                new CurrentUserServiceFalso(usuarioId ?? _usuario), new TenantActualFijo(tenantId), _almacen, new Clientes.UnitOfWorkFalso())
            .Handle(new GuardarFiltroCommand(pantalla, nombre, "{\"empresaId\":\"de-ese-tenant\"}"), CancellationToken.None);

    private async Task<IReadOnlyList<string>> LeerAsync(Guid? tenantId, string pantalla, Guid? usuarioId = null)
    {
        var contexto = new ConfiguracionQueryContextFalso();
        contexto.ListaFiltrosGuardados.AddRange(_almacen.Filtros);

        var filtros = await new ObtenerFiltrosGuardadosQueryHandler(
                contexto, new CurrentUserServiceFalso(usuarioId ?? _usuario), new TenantActualFijo(tenantId))
            .Handle(new ObtenerFiltrosGuardadosQuery(pantalla), CancellationToken.None);

        return filtros.Select(f => f.Nombre).ToList();
    }

    private Task<Result> EliminarAsync(Guid? tenantId, Guid filtroId) =>
        new EliminarFiltroGuardadoCommandHandler(
                new CurrentUserServiceFalso(_usuario), new TenantActualFijo(tenantId), _almacen, new Clientes.UnitOfWorkFalso())
            .Handle(new EliminarFiltroGuardadoCommand(filtroId), CancellationToken.None);

    [Fact]
    public async Task Lo_guardado_en_un_Tenant_se_lee_en_ese_Tenant()
    {
        (await GuardarAsync(_tenantA, PantallasConFiltrosGuardados.Clientes, "Críticos de A")).EsExitoso.Should().BeTrue();

        (await LeerAsync(_tenantA, PantallasConFiltrosGuardados.Clientes)).Should().Equal("Críticos de A");
    }

    [Fact]
    public async Task El_mismo_usuario_no_ve_en_un_Tenant_lo_que_guardo_en_otro()
    {
        await GuardarAsync(_tenantA, PantallasConFiltrosGuardados.Clientes, "Críticos de A");
        await GuardarAsync(_tenantB, PantallasConFiltrosGuardados.Clientes, "Críticos de B");

        (await LeerAsync(_tenantB, PantallasConFiltrosGuardados.Clientes)).Should().Equal(["Críticos de B"], "lo de A lleva identificadores de A");
        (await LeerAsync(_tenantA, PantallasConFiltrosGuardados.Clientes)).Should().Equal(["Críticos de A"], "y viceversa");
    }

    [Fact]
    public async Task Dentro_de_un_Tenant_cada_pantalla_sigue_viendo_solo_sus_filtros()
    {
        await GuardarAsync(_tenantA, PantallasConFiltrosGuardados.Clientes, "De clientes");
        await GuardarAsync(_tenantA, PantallasConFiltrosGuardados.Documentos, "De documentos");

        (await LeerAsync(_tenantA, PantallasConFiltrosGuardados.Documentos)).Should().Equal("De documentos");
    }

    [Fact]
    public async Task Dentro_de_un_Tenant_cada_usuario_sigue_viendo_solo_los_suyos()
    {
        await GuardarAsync(_tenantA, PantallasConFiltrosGuardados.Clientes, "Mío");

        (await LeerAsync(_tenantA, PantallasConFiltrosGuardados.Clientes, usuarioId: Guid.NewGuid())).Should().BeEmpty();
    }

    /// <summary>
    /// Las filas anteriores a la regla llevan el nombre de la pantalla a secas.
    /// No se sabe a qué Tenant pertenecían sus identificadores, así que no se
    /// leen desde ninguno — ni se reasignan ni se borran.
    /// </summary>
    [Fact]
    public async Task Una_fila_antigua_sin_Tenant_en_la_clave_no_se_lee_desde_ningun_Tenant()
    {
        _almacen.Agregar(new FiltroGuardado(_usuario, PantallasConFiltrosGuardados.Clientes, "Antiguo", "{}"));

        (await LeerAsync(_tenantA, PantallasConFiltrosGuardados.Clientes)).Should().BeEmpty();
        (await LeerAsync(_tenantB, PantallasConFiltrosGuardados.Clientes)).Should().BeEmpty();
        _almacen.Filtros.Should().ContainSingle("no leerla no es borrarla");
    }

    [Fact]
    public async Task Sin_Tenant_activo_no_se_lee_nada_ni_siquiera_las_filas_antiguas()
    {
        _almacen.Agregar(new FiltroGuardado(_usuario, PantallasConFiltrosGuardados.Clientes, "Antiguo", "{}"));
        await GuardarAsync(_tenantA, PantallasConFiltrosGuardados.Clientes, "De A");

        (await LeerAsync(null, PantallasConFiltrosGuardados.Clientes)).Should().BeEmpty();
        (await LeerAsync(Guid.Empty, PantallasConFiltrosGuardados.Clientes)).Should().BeEmpty("un Tenant vacío tampoco es un Tenant");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sin_Tenant_activo_no_se_guarda_y_el_fallo_es_legible(bool tenantVacio)
    {
        var resultado = await GuardarAsync(tenantVacio ? Guid.Empty : null, PantallasConFiltrosGuardados.Clientes, "Huérfano");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("FiltroGuardado.SinTenant");
        resultado.Error.Mensaje.Should().NotBeNullOrWhiteSpace();
        _almacen.Filtros.Should().BeEmpty("nunca se guarda con la clave antigua sin Tenant");
    }

    [Fact]
    public async Task Se_puede_eliminar_el_filtro_del_Tenant_activo()
    {
        var id = (await GuardarAsync(_tenantA, PantallasConFiltrosGuardados.Clientes, "De A")).Valor;

        (await EliminarAsync(_tenantA, id)).EsExitoso.Should().BeTrue();
        _almacen.Filtros.Should().BeEmpty();
    }

    /// <summary>
    /// Lo que no se puede ver no se puede borrar: el identificador de un filtro
    /// de otro Tenant puede llegar desde una pestaña que quedó abierta antes de
    /// cambiar de Tenant.
    /// </summary>
    [Fact]
    public async Task No_se_puede_eliminar_desde_un_Tenant_el_filtro_guardado_en_otro()
    {
        var id = (await GuardarAsync(_tenantA, PantallasConFiltrosGuardados.Clientes, "De A")).Valor;

        var resultado = await EliminarAsync(_tenantB, id);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("FiltroGuardado.NoEncontrado", "mismo mensaje que si no existiera");
        _almacen.Filtros.Should().ContainSingle();
    }

    [Fact]
    public async Task Sin_Tenant_activo_no_se_elimina_nada()
    {
        var id = (await GuardarAsync(_tenantA, PantallasConFiltrosGuardados.Clientes, "De A")).Valor;

        (await EliminarAsync(null, id)).EsFallido.Should().BeTrue();
        _almacen.Filtros.Should().ContainSingle();
    }

    [Fact]
    public async Task Una_fila_antigua_sin_Tenant_en_la_clave_tampoco_se_puede_eliminar()
    {
        var antiguo = new FiltroGuardado(_usuario, PantallasConFiltrosGuardados.Clientes, "Antiguo", "{}");
        _almacen.Agregar(antiguo);

        (await EliminarAsync(_tenantA, antiguo.Id)).EsFallido.Should().BeTrue();
        _almacen.Filtros.Should().ContainSingle();
    }

    /// <summary>
    /// Fija la forma de la clave con un literal, no con la propia función: si
    /// la función dejara de incluir el Tenant, los tests de arriba que la usan
    /// en los dos lados podrían seguir en verde.
    /// </summary>
    [Fact]
    public async Task La_clave_guardada_es_la_pantalla_mas_el_Tenant_activo()
    {
        var tenant = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        await GuardarAsync(tenant, PantallasConFiltrosGuardados.Trabajadores, "Con clave");

        _almacen.Filtros.Should().ContainSingle()
            .Which.Pantalla.Should().Be("Trabajadores@0f8fad5bd9cb469fa16570867728950e");
    }

    [Fact]
    public void Sin_Tenant_no_hay_clave()
    {
        PantallasConFiltrosGuardados.ClaveAlmacenada(PantallasConFiltrosGuardados.Clientes, null).Should().BeNull();
        PantallasConFiltrosGuardados.ClaveAlmacenada(PantallasConFiltrosGuardados.Clientes, Guid.Empty).Should().BeNull();
    }

    /// <summary>
    /// La columna admite 50 caracteres y no se amplía sin migración: toda
    /// pantalla admitida tiene que caber con el Tenant puesto. Añadir una
    /// pantalla de nombre largo a <see cref="PantallasConFiltrosGuardados.Admitidas"/>
    /// pone este test en rojo antes de que Postgres rechace el primer guardado.
    /// </summary>
    [Fact]
    public void La_clave_de_toda_pantalla_admitida_cabe_en_la_columna()
    {
        PantallasConFiltrosGuardados.LongitudMaximaDeLaClave.Should().Be(50, "es el ancho real de FiltrosGuardados.Pantalla; cambiarlo exige una migración");
        PantallasConFiltrosGuardados.Admitidas.Should().NotBeEmpty();

        foreach (var pantalla in PantallasConFiltrosGuardados.Admitidas)
        {
            var clave = PantallasConFiltrosGuardados.ClaveAlmacenada(pantalla, Guid.NewGuid());

            clave.Should().NotBeNull();
            clave!.Length.Should().BeLessThanOrEqualTo(
                PantallasConFiltrosGuardados.LongitudMaximaDeLaClave, $"la clave de «{pantalla}» tiene que caber en la columna");
        }
    }

    /// <summary>
    /// La clave de una pantalla en un Tenant no puede coincidir con la de otra
    /// pantalla ni con la de otro Tenant, y <c>EsDelTenant</c> solo reconoce las
    /// del suyo.
    /// </summary>
    [Fact]
    public void EsDelTenant_solo_reconoce_las_claves_de_ese_Tenant()
    {
        foreach (var pantalla in PantallasConFiltrosGuardados.Admitidas)
        {
            var claveDeA = PantallasConFiltrosGuardados.ClaveAlmacenada(pantalla, _tenantA)!;

            PantallasConFiltrosGuardados.EsDelTenant(claveDeA, _tenantA).Should().BeTrue();
            PantallasConFiltrosGuardados.EsDelTenant(claveDeA, _tenantB).Should().BeFalse();
            PantallasConFiltrosGuardados.EsDelTenant(claveDeA, null).Should().BeFalse();
            PantallasConFiltrosGuardados.EsDelTenant(pantalla, _tenantA).Should().BeFalse("la clave antigua sin Tenant no es de ningún Tenant");
        }
    }
}
