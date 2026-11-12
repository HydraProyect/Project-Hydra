using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// «Agrupar por» Plataforma CAE y Cliente empresarial, y el filtro «Estado en la plataforma» (P-1), contra PostgreSQL.
///
/// Los tests de Application corren sobre LINQ en memoria: no dicen si el join de la unión de cinco ramas con los
/// vínculos y el GroupBy de las cabeceras se traducen a SQL, ni si el alcance llega a esa traducción. Aquí la misma
/// consulta corre en Npgsql con un usuario de cartera limitada: un Cliente empresarial visible con su Centro, y otro
/// ajeno cuyo Centro tiene acreditaciones de Documentos del visible. Cada caso lleva su control sin restricción, para
/// que una ausencia distinga «el alcance lo excluye» de «no existe».
/// </summary>
public class DocumentosAgrupadosPorVinculoEnSqlTests : IAsyncLifetime
{
    private const string ClienteVisible = "Visible S.L.";
    private const string ClienteAjeno = "Fuera de cartera S.L.";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();

    private Guid _clienteVisibleId;
    private Guid _clienteAjenoId;
    private Guid _centroVisibleId;
    private Guid _plataformaCompartidaId;
    private Guid _plataformaSoloVisibleId;

    // Documentos del Cliente empresarial visible.
    private Guid _aceptadoYRechazadoId;   // Compartida (Centro visible): aceptada sin vigencia; Solo visible: rechazada.
    private Guid _pendienteId;            // Compartida (Centro visible): pendiente; Compartida (Centro ajeno): rechazada.
    private Guid _sinVinculoId;           // Sin acreditaciones.
    // Documento del Cliente empresarial ajeno.
    private Guid _ajenoId;                // Compartida (Centro ajeno): rechazada.

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var parametros = await contexto.ParametrosSistema.SingleOrDefaultAsync();
        if (parametros is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var visible = Empresa.CrearComoCliente(ClienteVisible, "B12345674", false, null, null);
        var ajeno = Empresa.CrearComoCliente(ClienteAjeno, "B87654323", false, null, null);
        var propia = new Empresa("Montajes Springfield S.L.", "B10380186");
        contexto.Empresas.AddRange(visible, ajeno, propia);

        var seguro = new TipoDocumento("Seguro RC", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(seguro);

        var compartida = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma compartida");
        var soloVisible = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma solo del visible");
        contexto.ProveedoresPlataformaCae.AddRange(compartida, soloVisible);
        await contexto.SaveChangesAsync();

        (_clienteVisibleId, _clienteAjenoId) = (visible.Id, ajeno.Id);
        (_plataformaCompartidaId, _plataformaSoloVisibleId) = (compartida.Id, soloVisible.Id);

        var centroVisible = new Centro(visible.Id, propia.Id, "Centro visible");
        var centroAjeno = new Centro(ajeno.Id, propia.Id, "Centro ajeno");
        contexto.Centros.AddRange(centroVisible, centroAjeno);
        await contexto.SaveChangesAsync();
        _centroVisibleId = centroVisible.Id;

        var canalVisible = CanalGestionDocumental.DePlataforma(centroVisible.Id, "Acceso", compartida.Id, null, null, null);
        var canalVisibleSolo = CanalGestionDocumental.DePlataforma(centroVisible.Id, "Otro acceso", soloVisible.Id, null, null, null);
        var canalAjeno = CanalGestionDocumental.DePlataforma(centroAjeno.Id, "Acceso", compartida.Id, null, null, null);
        contexto.CanalesGestionDocumental.AddRange(canalVisible, canalVisibleSolo, canalAjeno);

        var aceptadoYRechazado = Documento.DeCliente(visible.Id, seguro.Id, _hoy.AddDays(-10), VigenciaDocumento.NoCaduca);
        var pendiente = Documento.DeCliente(visible.Id, seguro.Id, _hoy.AddDays(-20), VigenciaDocumento.NoCaduca);
        var sinVinculo = Documento.DeCliente(visible.Id, seguro.Id, _hoy.AddDays(-30), VigenciaDocumento.NoCaduca);
        var delAjeno = Documento.DeCliente(ajeno.Id, seguro.Id, _hoy.AddDays(-40), VigenciaDocumento.NoCaduca);
        contexto.Documentos.AddRange(aceptadoYRechazado, pendiente, sinVinculo, delAjeno);
        await contexto.SaveChangesAsync();

        var aceptada = new AcreditacionDocumentoPlataforma(aceptadoYRechazado.Id, canalVisible.Id);
        aceptada.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);
        var rechazadaVisible = Rechazada(new AcreditacionDocumentoPlataforma(aceptadoYRechazado.Id, canalVisibleSolo.Id), "Motivo visible");
        var pendienteVisible = new AcreditacionDocumentoPlataforma(pendiente.Id, canalVisible.Id);
        var rechazadaEnElCentroAjeno = Rechazada(new AcreditacionDocumentoPlataforma(pendiente.Id, canalAjeno.Id), "Motivo del Centro ajeno");
        var rechazadaDelAjeno = Rechazada(new AcreditacionDocumentoPlataforma(delAjeno.Id, canalAjeno.Id), "Motivo del ajeno");
        contexto.AcreditacionesDocumentoPlataforma.AddRange(
            aceptada, rechazadaVisible, pendienteVisible, rechazadaEnElCentroAjeno, rechazadaDelAjeno);
        await contexto.SaveChangesAsync();

        (_aceptadoYRechazadoId, _pendienteId, _sinVinculoId, _ajenoId) = (aceptadoYRechazado.Id, pendiente.Id, sinVinculo.Id, delAjeno.Id);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private static AcreditacionDocumentoPlataforma Rechazada(AcreditacionDocumentoPlataforma acreditacion, string motivo)
    {
        acreditacion.MarcarSubida();
        acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, motivo, DateTime.UtcNow);
        return acreditacion;
    }

    [Fact]
    public async Task Por_Plataforma_CAE_las_filas_y_las_cabeceras_salen_de_SQL_y_solo_cuentan_los_Centros_de_la_cartera()
    {
        var resultado = await ConsultarAsync(Agrupado(AgrupacionDocumentos.PlataformaCae), CarteraLimitada());

        resultado.TotalElementos.Should().Be(3, "cuenta Documentos del Cliente empresarial visible, no filas");
        resultado.Elementos.Select(d => (d.Id, d.Grupo!.Clave)).Should().BeEquivalentTo(new[]
        {
            (_aceptadoYRechazadoId, _plataformaCompartidaId.ToString()),
            (_aceptadoYRechazadoId, _plataformaSoloVisibleId.ToString()),
            (_pendienteId, _plataformaCompartidaId.ToString()),
            (_sinVinculoId, ObtenerDocumentosQuery.GrupoSin),
        });
        resultado.Elementos.Select(d => d.Grupo!.Acreditacion?.UltimoMotivoRechazo)
            .Should().NotContain("Motivo del Centro ajeno").And.NotContain("Motivo del ajeno");

        var compartida = Resumen(resultado, _plataformaCompartidaId.ToString());
        compartida.Total.Should().Be(2, "la acreditación del Centro ajeno no cuenta");
        compartida.EnPlataforma.Should().BeEquivalentTo(new Dictionary<EstadoEnPlataformaFiltro, int>
        {
            [EstadoEnPlataformaFiltro.SinVigencia] = 1,
            [EstadoEnPlataformaFiltro.PendienteDeSubir] = 1,
        });
        Resumen(resultado, _plataformaSoloVisibleId.ToString()).EnPlataforma.Should().BeEquivalentTo(
            new Dictionary<EstadoEnPlataformaFiltro, int> { [EstadoEnPlataformaFiltro.Rechazado] = 1 });
        Resumen(resultado, ObtenerDocumentosQuery.GrupoSin).Total.Should().Be(1);

        // Control: sin restricción, la acreditación del Centro ajeno y el Documento del ajeno sí cuentan.
        var sinRestriccion = await ConsultarAsync(Agrupado(AgrupacionDocumentos.PlataformaCae), new AlcanceDatosServiceFalso());
        Resumen(sinRestriccion, _plataformaCompartidaId.ToString()).Total.Should().Be(4);
        sinRestriccion.Elementos.Select(d => d.Id).Should().Contain(_ajenoId);
    }

    [Fact]
    public async Task Por_Cliente_empresarial_no_aparece_el_grupo_de_un_Cliente_empresarial_fuera_de_la_cartera()
    {
        var resultado = await ConsultarAsync(Agrupado(AgrupacionDocumentos.ClienteEmpresarial), CarteraLimitada());

        resultado.ResumenPorGrupo!.Keys.Should().BeEquivalentTo([_clienteVisibleId.ToString(), ObtenerDocumentosQuery.GrupoSin]);
        resultado.ResumenPorGrupo.Values.OfType<ResumenDeGrupoDocumental>().Select(r => r.Nombre).Should().NotContain(ClienteAjeno);
        Resumen(resultado, _clienteVisibleId.ToString()).Total.Should().Be(2);
        resultado.Elementos.Select(d => (d.Id, d.Grupo!.Clave)).Should().BeEquivalentTo(new[]
        {
            (_aceptadoYRechazadoId, _clienteVisibleId.ToString()),
            (_pendienteId, _clienteVisibleId.ToString()),
            (_sinVinculoId, ObtenerDocumentosQuery.GrupoSin),
        });

        var sinRestriccion = await ConsultarAsync(Agrupado(AgrupacionDocumentos.ClienteEmpresarial), new AlcanceDatosServiceFalso());
        sinRestriccion.ResumenPorGrupo!.Keys.Should().Contain(_clienteAjenoId.ToString(), "control: el grupo existe sin restricción");
    }

    /// <summary>
    /// El Documento pendiente solo está rechazado en el Centro ajeno: si el filtro mirase todas sus acreditaciones,
    /// delataría un rechazo que el usuario no puede ver.
    /// </summary>
    [Fact]
    public async Task Estado_en_la_plataforma_filtra_solo_por_las_acreditaciones_de_los_Centros_de_la_cartera()
    {
        var rechazados = await ConsultarAsync(Filtrado(EstadoEnPlataformaFiltro.Rechazado), CarteraLimitada());
        rechazados.TotalElementos.Should().Be(1);
        rechazados.Elementos.Select(d => d.Id).Should().Equal(_aceptadoYRechazadoId);

        var rechazadosAgrupados = await ConsultarAsync(
            Agrupado(AgrupacionDocumentos.PlataformaCae, EstadoEnPlataformaFiltro.Rechazado), CarteraLimitada());
        rechazadosAgrupados.Elementos.Select(d => (d.Id, d.Grupo!.Clave)).Should().Equal(
            (_aceptadoYRechazadoId, _plataformaSoloVisibleId.ToString()));
        rechazadosAgrupados.ResumenPorGrupo!.Keys.Should().BeEquivalentTo([_plataformaSoloVisibleId.ToString()]);

        var sinVigencia = await ConsultarAsync(
            Agrupado(AgrupacionDocumentos.ClienteEmpresarial, EstadoEnPlataformaFiltro.SinVigencia), CarteraLimitada());
        sinVigencia.Elementos.Select(d => d.Id).Should().Equal(_aceptadoYRechazadoId);

        // Control: sin restricción, los tres Documentos con un rechazo aparecen.
        var sinRestriccion = await ConsultarAsync(Filtrado(EstadoEnPlataformaFiltro.Rechazado), new AlcanceDatosServiceFalso());
        sinRestriccion.Elementos.Select(d => d.Id).Should().BeEquivalentTo([_aceptadoYRechazadoId, _pendienteId, _ajenoId]);
    }

    private static ObtenerDocumentosQuery Agrupado(AgrupacionDocumentos agrupacion, params EstadoEnPlataformaFiltro[] estados) =>
        new(null, null, null, TamanoPagina: 100, AgruparPor: agrupacion, EstadosEnPlataforma: estados.Length > 0 ? estados : null);

    private static ObtenerDocumentosQuery Filtrado(params EstadoEnPlataformaFiltro[] estados) =>
        new(null, null, null, TamanoPagina: 100, EstadosEnPlataforma: estados);

    private static ResumenDeGrupoDocumental Resumen(ResultadoPaginado<DocumentoListaDto> resultado, string clave) =>
        resultado.ResumenPorGrupo.Should().ContainKey(clave).WhoseValue.Should().BeOfType<ResumenDeGrupoDocumental>().Subject;

    private AlcanceDatosServiceFalso CarteraLimitada() =>
        new(clienteIds: [_clienteVisibleId], centroIds: [_centroVisibleId], centroIdsParaGestion: [_centroVisibleId]);

    private async Task<ResultadoPaginado<DocumentoListaDto>> ConsultarAsync(
        ObtenerDocumentosQuery consulta, AlcanceDatosServiceFalso alcance)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerDocumentosQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto, alcance, contexto, contexto);
        return await handler.Handle(consulta, CancellationToken.None);
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
