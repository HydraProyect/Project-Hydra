using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Visitas;

/// <summary>
/// <c>OrdenarPor = PorGestionar</c> de ObtenerVisitasQuery, contra PostgreSQL real. «Por
/// gestionar» es el estado guardado en la Visita (sin fecha de documentación gestionada), no
/// un cálculo sobre los documentos: se ordena y se pagina en SQL, y una Visita con todos los
/// documentos vigentes sigue por gestionar hasta que se marca. El DTO lleva además la
/// antelación sellada en la Visita.
/// </summary>
public class ObtenerVisitasQueryOrdenPorGestionarTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();
    private Guid _porGestionarCercana;
    private Guid _porGestionarLejana;
    private Guid _gestionada;
    private Guid _vigenteSinGestionar;
    private Guid _sinGestionCae;
    private Guid _cancelada;

    public async Task InitializeAsync()
    {
        await using var c = CrearContexto();
        await c.Database.MigrateAsync();
        c.ParametrosSistema.Add(new ParametroSistema(30, 15, horasAvisoVisita: 48, horasCriticasVisita: 24));

        var cliente = Empresa.CrearComoCliente("Cliente Orden Visita S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Orden Visita S.L.", "B87654323");
        c.Empresas.AddRange(cliente, empresa);
        await c.SaveChangesAsync();

        var conGestion = new Centro(cliente.Id, empresa.Id, "Centro con gestión");
        var sinGestion = new Centro(cliente.Id, empresa.Id, "Centro sin gestión");
        sinGestion.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        c.Centros.AddRange(conGestion, sinGestion);
        var ana = Trabajador.DeEmpresa(empresa.Id, "Ana", "Ruiz", "12345678Z");
        c.Trabajadores.Add(ana);
        await c.SaveChangesAsync();

        // Sin trabajadores y sin tipos obligatorios → documentos «completos» las dos; solo
        // una está marcada como gestionada. La otra es el caso que decide la regla nueva.
        var gestionada = new Visita(conGestion.Id, _hoy.AddDays(1), _hoy.AddDays(1), null);
        gestionada.MarcarDocumentacionGestionada(DateTime.UtcNow);
        var vigenteSinGestionar = new Visita(conGestion.Id, _hoy.AddDays(6), _hoy.AddDays(6), null);
        // Con una trabajadora sin ningún documento → por gestionar.
        var lejana = new Visita(conGestion.Id, _hoy.AddDays(8), _hoy.AddDays(8), null);
        var cercana = new Visita(conGestion.Id, _hoy.AddDays(5), _hoy.AddDays(5), null);
        var sinGestionCae = new Visita(sinGestion.Id, _hoy.AddDays(2), _hoy.AddDays(2), null);
        var cancelada = new Visita(conGestion.Id, _hoy.AddDays(3), _hoy.AddDays(3), null);
        cancelada.Cancelar(DateTime.UtcNow, "Obra aplazada");

        // Antelación sellada en la cercana: la lista la ve igual que el detalle.
        var solicitud = DateTime.UtcNow.AddDays(-1);
        cercana.RegistrarOrigenSolicitud(Guid.NewGuid(), solicitud);
        cercana.MarcarExpedienteCompleto(solicitud.AddHours(3),
            new ResultadoAntelacion(25m, 22m, 3m, TramoAntelacion.Expres, AtribucionUrgencia.SolicitudTardiaCliente));

        c.Visitas.AddRange(gestionada, vigenteSinGestionar, lejana, cercana, sinGestionCae, cancelada);
        await c.SaveChangesAsync();
        c.VisitasTrabajadores.AddRange(
            new VisitaTrabajador(lejana.Id, ana.Id), new VisitaTrabajador(cercana.Id, ana.Id),
            new VisitaTrabajador(sinGestionCae.Id, ana.Id), new VisitaTrabajador(cancelada.Id, ana.Id));
        await c.SaveChangesAsync();

        (_gestionada, _vigenteSinGestionar, _porGestionarLejana, _porGestionarCercana, _sinGestionCae, _cancelada) =
            (gestionada.Id, vigenteSinGestionar.Id, lejana.Id, cercana.Id, sinGestionCae.Id, cancelada.Id);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private sealed record Lectura(List<VisitaListaDto> Elementos, int Total);

    private async Task<Lectura> LeerAsync(bool descendente, int pagina = 1, int tamano = 20, string? ordenarPor = nameof(VisitaListaDto.PorGestionar))
    {
        await using var lectura = CrearContexto();
        var r = await new ObtenerVisitasQueryHandler(lectura, lectura, lectura, lectura, lectura, lectura, new AlcanceDatosServiceFalso(), lectura)
            .Handle(new ObtenerVisitasQuery(null, SoloActivas: false, NotificadoCliente: null, Pagina: pagina, TamanoPagina: tamano,
                OrdenarPor: ordenarPor, Descendente: descendente), CancellationToken.None);
        return new Lectura(r.Elementos.ToList(), r.TotalElementos);
    }

    [Fact]
    public async Task Descendente_pone_primero_las_por_gestionar_y_dentro_de_cada_grupo_la_que_entra_antes()
    {
        var r = await LeerAsync(descendente: true);

        r.Elementos.Select(v => v.Id).Should().Equal(
            _porGestionarCercana, _vigenteSinGestionar, _porGestionarLejana,   // por gestionar, por fecha
            _gestionada, _sinGestionCae, _cancelada);                          // el resto, por fecha
    }

    [Fact]
    public async Task Ascendente_las_deja_al_final()
    {
        var r = await LeerAsync(descendente: false);

        r.Elementos.Select(v => v.Id).Should().Equal(
            _gestionada, _sinGestionCae, _cancelada,
            _porGestionarCercana, _vigenteSinGestionar, _porGestionarLejana);
    }

    /// <summary>
    /// La regla del 2026-10-09: con todos los documentos vigentes la Visita sigue «Por
    /// gestionar» hasta que se envía el paquete o se marca; y marcada, deja de estarlo aunque
    /// los documentos digan lo mismo que antes.
    /// </summary>
    [Fact]
    public async Task Por_gestionar_sale_del_estado_guardado_y_no_de_que_los_documentos_esten_vigentes()
    {
        var r = await LeerAsync(descendente: true);

        var sinGestionar = r.Elementos.Single(v => v.Id == _vigenteSinGestionar);
        sinGestionar.DocumentacionCompleta.Should().BeTrue("control: sus documentos están en regla");
        sinGestionar.DocumentacionGestionadaEnUtc.Should().BeNull();
        sinGestionar.PorGestionar.Should().BeTrue();

        var gestionada = r.Elementos.Single(v => v.Id == _gestionada);
        gestionada.DocumentacionCompleta.Should().BeTrue("control: mismos documentos que la anterior");
        gestionada.DocumentacionGestionadaEnUtc.Should().NotBeNull();
        gestionada.PorGestionar.Should().BeFalse();
    }

    [Fact]
    public async Task Una_cancelada_o_de_centro_sin_gestion_no_cuenta_como_por_gestionar_aunque_su_documentacion_este_incompleta()
    {
        var r = await LeerAsync(descendente: true);

        var cancelada = r.Elementos.Single(v => v.Id == _cancelada);
        cancelada.DocumentacionCompleta.Should().BeFalse();
        cancelada.PorGestionar.Should().BeFalse();
        r.Elementos.Single(v => v.Id == _sinGestionCae).PorGestionar.Should().BeFalse();
        r.Elementos.Count(v => v.PorGestionar).Should().Be(3);
    }

    [Fact]
    public async Task La_pagina_se_corta_despues_de_ordenar_y_el_total_es_el_del_filtro()
    {
        var segunda = await LeerAsync(descendente: true, pagina: 2, tamano: 2);

        segunda.Total.Should().Be(6);
        segunda.Elementos.Select(v => v.Id).Should().Equal(_porGestionarLejana, _gestionada);
    }

    [Fact]
    public async Task Sin_ese_orden_la_consulta_sigue_paginando_por_fecha()
    {
        var r = await LeerAsync(descendente: false, tamano: 2, ordenarPor: null);

        r.Elementos.Select(v => v.Id).Should().Equal(_gestionada, _sinGestionCae);
    }

    [Fact]
    public async Task La_lista_lleva_la_antelacion_sellada_y_null_si_no_la_hay()
    {
        var r = await LeerAsync(descendente: true);

        var medida = r.Elementos.Single(v => v.Id == _porGestionarCercana);
        (medida.Tramo, medida.AntelacionNominalHoras, medida.AntelacionEfectivaHoras)
            .Should().Be((TramoAntelacion.Expres, 25m, 22m));
        r.Elementos.Single(v => v.Id == _gestionada).Tramo.Should().BeNull();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
