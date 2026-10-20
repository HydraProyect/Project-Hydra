using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Application.Empresas.Queries.ObtenerVigenciaEmpresa;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Empresas;

/// <summary>
/// La matriz «Vigencia» de Empresa 360 contra PostgreSQL con RLS: qué exige cada Centro a cada Trabajador, qué copia representa
/// al par, y que solo cuentan los Trabajadores de ESA Empresa y los Centros que el actor puede ver.
/// </summary>
public class ObtenerVigenciaEmpresaQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private sealed record Escenario(Guid EmpresaId, Guid OtraEmpresaId, Guid CentroId, Guid NoraId, Guid PabloId, Guid TipoAptitudId, Guid TipoCarretillaId);

    private async Task<Escenario> SembrarAsync(ModalidadGestionCae gestion = ModalidadGestionCae.ConGestionCae, int? periodicidadAptitudDelCentro = null)
    {
        var hoy = DiaDeNegocio.Hoy();
        await using var contexto = CrearContexto();
        var cliente = Empresa.CrearComoCliente("Cliente Vigencia S.L.", "B10380194", false, null, null);
        var empresa = new Empresa("Empresa Vigencia S.L.", "B10380186");
        var otra = new Empresa("Otra Empresa S.L.", "B10380178");
        contexto.Empresas.AddRange(cliente, empresa, otra);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Planta Vigencia");
        centro.EstablecerGestionCae(gestion);
        contexto.Centros.Add(centro);

        contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));
        var delegacion = new DelegacionEmpresa(empresa.Id, "Bilbao");
        contexto.DelegacionesEmpresa.Add(delegacion);

        var aptitud = new TipoDocumento("Reconocimiento médico", 12, true, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        var carretilla = new TipoDocumento("Carretilla elevadora", 60, true, 2, AmbitoAplicacion.Trabajador);
        contexto.TiposDocumento.AddRange(aptitud, carretilla);
        await contexto.SaveChangesAsync();
        if (periodicidadAptitudDelCentro is { } meses)
            contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(aptitud.Id, centro.Id, periodicidadEspecialMeses: meses));

        var nora = Trabajador.DeEmpresa(empresa.Id, "Nora", "Vidal", "22334455Y");
        nora.FijarDelegacionEmpresaYOficio(delegacion, null);
        var pablo = Trabajador.DeEmpresa(empresa.Id, "Pablo", "Zubiri", "33445566R");
        // Con fecha de baja en la ficha (dato de plataformas): sigue siendo un Trabajador activo, como en la pestaña Trabajadores.
        pablo.ActualizarFicha(null, null, null, null, null, null, hoy.AddDays(-1), null, null, null, null);
        var ajeno = Trabajador.DeEmpresa(otra.Id, "Ajeno", "Lasa", "44556677L");
        contexto.Trabajadores.AddRange(nora, pablo, ajeno);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.AddRange(
            new Asignacion(nora.Id, centro.Id, hoy.AddYears(-1)),
            new Asignacion(pablo.Id, centro.Id, hoy.AddYears(-1)),
            new Asignacion(ajeno.Id, centro.Id, hoy.AddYears(-1)));
        contexto.Documentos.AddRange(
            Documento.DeTrabajador(nora.Id, aptitud.Id, hoy.AddDays(-165), VigenciaDocumento.VenceEl(hoy.AddDays(200))),
            Documento.DeTrabajador(nora.Id, carretilla.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(500))),
            Documento.DeTrabajador(ajeno.Id, aptitud.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(50))));
        await contexto.SaveChangesAsync();

        return new Escenario(empresa.Id, otra.Id, centro.Id, nora.Id, pablo.Id, aptitud.Id, carretilla.Id);
    }

    private async Task<VigenciaEmpresaDto> ConsultarAsync(Guid empresaId, AlcanceDatosServiceFalso? alcance = null)
    {
        await using var consulta = CrearContexto();
        var handler = new ObtenerVigenciaEmpresaQueryHandler(
            consulta, consulta, consulta, consulta, consulta, consulta, consulta, alcance ?? new AlcanceDatosServiceFalso());
        return await handler.Handle(new ObtenerVigenciaEmpresaQuery(empresaId), CancellationToken.None);
    }

    [Fact]
    public async Task La_matriz_cruza_Trabajadores_de_la_Empresa_con_lo_que_exigen_sus_Centros()
    {
        var e = await SembrarAsync();

        var matriz = await ConsultarAsync(e.EmpresaId);

        matriz.UmbralPorVencerDias.Should().BeGreaterThan(0);
        matriz.Centros.Should().ContainSingle().Which.Should().Be(new VigenciaCentroDto(e.CentroId, "Planta Vigencia", "Cliente Vigencia S.L."));
        matriz.Columnas.Select(c => (c.Nombre, c.Grupo, c.Basico)).Should().Equal(
            ("Reconocimiento médico", GrupoColumnaVigencia.Basicos, (TipoDocumentoBase?)TipoDocumentoBase.AptitudMedica),
            ("Carretilla elevadora", GrupoColumnaVigencia.Opcional, null));
        matriz.Columnas[0].ExigidoPor.Should().ContainSingle().Which.Should().Be(new VigenciaExigenciaCentroDto(e.CentroId, 12));
        matriz.Columnas[1].ExigidoPor.Should().BeEmpty("ningún Centro lo exige: es opcional");

        matriz.Filas.Select(f => f.Nombre).Should().Equal("Nora Vidal", "Pablo Zubiri");
        var nora = matriz.Filas[0];
        nora.DelegacionEmpresaNombre.Should().Be("Bilbao");
        nora.CentroIds.Should().Equal(e.CentroId);
        nora.Celdas[0].Estado.Should().Be(EstadoCeldaVigencia.Vigente);
        nora.Celdas[0].ExigidaPorCentroIds.Should().Equal(e.CentroId);
        nora.Celdas[0].FechaVencimiento.Should().Be(DiaDeNegocio.Hoy().AddDays(200));
        nora.Celdas[0].FechaEmision.Should().Be(DiaDeNegocio.Hoy().AddDays(-165));
        nora.Celdas[1].Estado.Should().Be(EstadoCeldaVigencia.Vigente);
        nora.Celdas[1].ExigidaPorCentroIds.Should().BeEmpty();

        var pablo = matriz.Filas[1];
        pablo.Celdas[0].Estado.Should().Be(EstadoCeldaVigencia.Falta, "el Centro lo exige y no tiene documento");
        pablo.Celdas[0].DocumentoId.Should().BeNull();
        pablo.Celdas[1].Estado.Should().Be(EstadoCeldaVigencia.NoAplica);
    }

    [Fact]
    public async Task La_periodicidad_del_Centro_viaja_en_la_columna_y_manda_sobre_la_fecha_del_documento()
    {
        // El tipo dura 12 meses; este Centro pide 24. La regla del dominio (VencimientoEfectivo) dice emisión + 24 meses.
        var e = await SembrarAsync(periodicidadAptitudDelCentro: 24);

        var matriz = await ConsultarAsync(e.EmpresaId);

        var columna = matriz.Columnas[0];
        columna.ExigidoPor.Should().ContainSingle().Which.Should().Be(new VigenciaExigenciaCentroDto(e.CentroId, 24, 24));
        var nora = matriz.Filas.Single(f => f.Nombre == "Nora Vidal");
        var resultado = VigenciaEnAlcance.Resolver(columna, nora.Celdas[0], null, DiaDeNegocio.Hoy(), matriz.UmbralPorVencerDias);
        resultado.Fecha.Should().Be(DiaDeNegocio.Hoy().AddDays(-165).AddMonths(24));
        resultado.Estado.Should().Be(EstadoCeldaVigencia.Vigente);
    }

    [Fact]
    public async Task Un_Centro_sin_gestion_CAE_no_exige_nada_y_su_columna_no_sale_si_nadie_la_tiene()
    {
        var e = await SembrarAsync(ModalidadGestionCae.SinGestionCae);

        var matriz = await ConsultarAsync(e.EmpresaId);

        matriz.Columnas.Select(c => c.Nombre).Should().Equal("Reconocimiento médico", "Carretilla elevadora");
        matriz.Columnas.Should().OnlyContain(c => c.ExigidoPor.Count == 0);
        matriz.Filas.Single(f => f.Nombre == "Pablo Zubiri").Celdas.Should().OnlyContain(c => c.Estado == EstadoCeldaVigencia.NoAplica);
    }

    [Fact]
    public async Task Una_Empresa_fuera_de_alcance_no_devuelve_nada()
    {
        var e = await SembrarAsync();

        var matriz = await ConsultarAsync(e.EmpresaId, new AlcanceDatosServiceFalso(empresaIds: [e.OtraEmpresaId]));

        matriz.Filas.Should().BeEmpty();
        matriz.Columnas.Should().BeEmpty();
        matriz.Centros.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Centro_fuera_de_alcance_no_exige_ni_se_lista()
    {
        var e = await SembrarAsync();

        var matriz = await ConsultarAsync(e.EmpresaId, new AlcanceDatosServiceFalso(centroIds: []));

        matriz.Centros.Should().BeEmpty();
        matriz.Filas.Should().HaveCount(2);
        matriz.Filas.SelectMany(f => f.CentroIds).Should().BeEmpty();
        matriz.Columnas.Should().OnlyContain(c => c.ExigidoPor.Count == 0);
    }

    [Fact]
    public async Task Un_Trabajador_fuera_de_cartera_no_sale()
    {
        var e = await SembrarAsync();

        var matriz = await ConsultarAsync(e.EmpresaId, new AlcanceDatosServiceFalso(trabajadorIds: [e.NoraId]));

        matriz.Filas.Should().ContainSingle().Which.Nombre.Should().Be("Nora Vidal");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }
}
