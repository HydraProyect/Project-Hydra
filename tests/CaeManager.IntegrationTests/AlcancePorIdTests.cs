using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Domain.Common;
using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentacionBaseTrabajadores;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Verifica el fix del Issue #18 (IDOR): las consultas `*PorId*` deben
/// respetar el alcance por cartera de <see cref="IAlcanceDatosService"/> —
/// un Id fuera de la cartera del usuario actual debe comportarse
/// exactamente igual que "no existe" (null), nunca devolver el dato ni un
/// error explícito que confirme que la fila existe. Usa SQLite real (no
/// in-memory) porque las consultas afectadas usan LINQ contra
/// IApplicationDbContext, igual criterio que MigracionesTests.
/// </summary>
public class AlcancePorIdTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _dbContext = null!;

    private Empresa _clienteVisible = null!;
    private Empresa _clienteAjeno = null!;
    private Documento _documentoDeTrabajadorVisible = null!;
    private Documento _documentoDeTrabajadorAjeno = null!;
    private Guid _trabajadorVisibleId;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = TenantSeedData.IdPorDefecto };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        _clienteVisible = Empresa.CrearComoCliente("Cadena Industrial Iberia S.A.", "B12345674", true, null, null);
        _clienteAjeno = Empresa.CrearComoCliente("Otro cliente, de otra cartera", "P1234567D", false, null, null);
        _dbContext.Empresas.AddRange(_clienteVisible, _clienteAjeno);

        var empresa = new Empresa("Ibertec S.A.");
        _dbContext.Empresas.Add(empresa);

        var centroVisible = new Centro(_clienteVisible.Id, empresa.Id, "Planta Sevilla");
        var centroAjeno = new Centro(_clienteAjeno.Id, empresa.Id, "Planta de otra cartera");
        _dbContext.Centros.AddRange(centroVisible, centroAjeno);

        var trabajadorVisible = Trabajador.DeEmpresa(empresa.Id, "Alvaro", "Sanchez Martin", "77189989B");
        var trabajadorAjeno = Trabajador.DeEmpresa(empresa.Id, "Otro", "Trabajador Ajeno", "12345678Z");
        _dbContext.Trabajadores.AddRange(trabajadorVisible, trabajadorAjeno);
        _trabajadorVisibleId = trabajadorVisible.Id;

        _dbContext.Asignaciones.AddRange(
            new Asignacion(trabajadorVisible.Id, centroVisible.Id, DiaDeNegocio.Hoy()),
            new Asignacion(trabajadorAjeno.Id, centroAjeno.Id, DiaDeNegocio.Hoy()));

        await _dbContext.SaveChangesAsync();

        // Documento de vigilancia de la salud — el caso más sensible del Issue #18:
        // un DNI/Id de Trabajador fuera de cartera no debe poder leerse por Id.
        // El tipo se elige por nombre, nunca «el primero que devuelva la base»: sin ORDER BY
        // PostgreSQL puede devolver «Certificado de aptitud médica», que comparte nombre
        // canónico de fichero e indicador de documentación base con los tipos médicos que
        // crean los tests de abajo, y este documento pasaría a contar como uno anterior
        // del mismo día (fallo intermitente en la cola de fusión, 2026-10-09).
        var tipoDocumentoTrabajador = await _dbContext.TiposDocumento
            .SingleAsync(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador && t.Nombre == "Primeros auxilios");

        _documentoDeTrabajadorVisible = Documento.DeTrabajador(
            trabajadorVisible.Id, tipoDocumentoTrabajador.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca);
        _documentoDeTrabajadorAjeno = Documento.DeTrabajador(
            trabajadorAjeno.Id, tipoDocumentoTrabajador.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca);
        _dbContext.Documentos.AddRange(_documentoDeTrabajadorVisible, _documentoDeTrabajadorAjeno);

        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Devuelve_el_cliente_cuando_esta_dentro_de_la_cartera_visible()
    {
        var alcance = new AlcanceDatosServiceFalso(clienteIds: [_clienteVisible.Id]);
        var handler = new ObtenerClientePorIdQueryHandler(_dbContext, alcance, PoliticaNotaInternaPruebas.Con("GestorCae"));

        var resultado = await handler.Handle(new ObtenerClientePorIdQuery(_clienteVisible.Id), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.RazonSocial.Should().Be(_clienteVisible.RazonSocial);
    }

    [Fact]
    public async Task Devuelve_null_al_pedir_un_cliente_fuera_de_la_cartera_visible_aunque_exista()
    {
        // Cartera restringida a _clienteVisible únicamente — _clienteAjeno existe
        // de verdad en la base de datos, pero no debe ser legible por este usuario.
        var alcance = new AlcanceDatosServiceFalso(clienteIds: [_clienteVisible.Id]);
        var handler = new ObtenerClientePorIdQueryHandler(_dbContext, alcance, PoliticaNotaInternaPruebas.Con("GestorCae"));

        var resultado = await handler.Handle(new ObtenerClientePorIdQuery(_clienteAjeno.Id), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Un_rol_sin_restriccion_ve_cualquier_cliente()
    {
        var alcance = new AlcanceDatosServiceFalso(); // todo null = sin restricción (Administrador/DireccionCae/Consulta)
        var handler = new ObtenerClientePorIdQueryHandler(_dbContext, alcance, PoliticaNotaInternaPruebas.Con("GestorCae"));

        var resultado = await handler.Handle(new ObtenerClientePorIdQuery(_clienteAjeno.Id), CancellationToken.None);

        resultado.Should().NotBeNull();
    }

    [Fact]
    public async Task Devuelve_el_documento_de_un_trabajador_dentro_de_la_cartera_visible()
    {
        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorVisibleId]);
        var handler = new ObtenerDocumentoPorIdQueryHandler(
            _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, alcance);

        var resultado = await handler.Handle(
            new ObtenerDocumentoPorIdQuery(_documentoDeTrabajadorVisible.Id), CancellationToken.None);

        resultado.Should().NotBeNull();
    }

    [Fact]
    public async Task Devuelve_null_al_pedir_el_documento_de_salud_de_un_trabajador_fuera_de_cartera()
    {
        // Este es el caso concreto del Issue #18: sin el fix, un Gestor CAE con
        // cartera restringida a un trabajador podía leer igualmente el PDF de
        // vigilancia de la salud de un trabajador de otra cartera con solo
        // conocer/adivinar el Guid del Documento.
        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorVisibleId]);
        var handler = new ObtenerDocumentoPorIdQueryHandler(
            _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, alcance);

        var resultado = await handler.Handle(
            new ObtenerDocumentoPorIdQuery(_documentoDeTrabajadorAjeno.Id), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Una_cartera_vacia_no_ve_ningun_documento_de_trabajador()
    {
        // Lista vacía = cartera sin asignar todavía (p. ej. un Gestor CAE recién
        // creado) — nunca debe confundirse con "sin restricción".
        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: []);
        var handler = new ObtenerDocumentoPorIdQueryHandler(
            _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, alcance);

        var resultado = await handler.Handle(
            new ObtenerDocumentoPorIdQuery(_documentoDeTrabajadorVisible.Id), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task La_documentacion_base_solo_sale_de_los_trabajadores_visibles_y_cuenta_sus_documentos()
    {
        var tipo = await _dbContext.TiposDocumento.FirstOrDefaultAsync(t => t.Nombre == "Entrega de EPI")
            ?? new TipoDocumento("Entrega de EPI", 12, true, 95, AmbitoAplicacion.Trabajador);
        if (_dbContext.Entry(tipo).State == EntityState.Detached)
            _dbContext.TiposDocumento.Add(tipo);
        await _dbContext.SaveChangesAsync();
        var trabajadorAjenoId = _documentoDeTrabajadorAjeno.TrabajadorId!.Value;
        _dbContext.Documentos.AddRange(
            Documento.DeTrabajador(_trabajadorVisibleId, tipo.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca),
            Documento.DeTrabajador(trabajadorAjenoId, tipo.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca));
        await _dbContext.SaveChangesAsync();

        var handler = new ObtenerDocumentacionBaseTrabajadoresQueryHandler(
            _dbContext, _dbContext, _dbContext, new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorVisibleId]));

        var resultado = await handler.Handle(
            new ObtenerDocumentacionBaseTrabajadoresQuery([_trabajadorVisibleId, trabajadorAjenoId]), CancellationToken.None);

        resultado.Keys.Should().Equal(_trabajadorVisibleId);
        var epi = resultado[_trabajadorVisibleId].Indicadores.Single(i => i.Tipo == TipoDocumentoBase.EntregaEpi);
        epi.Estado.Should().Be(EstadoIndicadorBase.Vigente);
        resultado[_trabajadorVisibleId].Indicadores.Where(i => i.Tipo != TipoDocumentoBase.EntregaEpi)
            .Should().OnlyContain(i => i.Estado == EstadoIndicadorBase.Falta);
    }

    [Fact]
    public async Task El_nombre_de_descarga_lleva_Apellidos_Nombre_sin_DNI_y_v2_para_el_segundo_documento_del_mismo_dia()
    {
        var segundo = Documento.DeTrabajador(
            _trabajadorVisibleId, _documentoDeTrabajadorVisible.TipoDocumentoId, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca);
        _dbContext.Documentos.Add(segundo);
        await _dbContext.SaveChangesAsync();

        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorVisibleId]);
        var handler = new ObtenerDocumentoPorIdQueryHandler(
            _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, alcance);

        var primero = await handler.Handle(new ObtenerDocumentoPorIdQuery(_documentoDeTrabajadorVisible.Id), CancellationToken.None);
        var conSufijo = await handler.Handle(new ObtenerDocumentoPorIdQuery(segundo.Id), CancellationToken.None);

        primero!.NombreArchivoDescarga.Should().StartWith("Sanchez Martin Alvaro - ").And.EndWith($"emitido {DiaDeNegocio.Hoy():yyyy-MM-dd}.pdf");
        conSufijo!.NombreArchivoDescarga.Should().EndWith($"emitido {DiaDeNegocio.Hoy():yyyy-MM-dd}_v2.pdf");
        primero.NombreArchivoDescarga.Should().NotContain("77189989B");
    }

    [Fact]
    public async Task Dos_tipos_con_el_mismo_nombre_canonico_el_mismo_dia_reciben_sufijo_distinto()
    {
        // «Aptitud médica» y «Reconocimiento médico» son TipoDocumentoId distintos que dan el mismo
        // componente de fichero: el ordinal debe agruparlos igual que el nombre, o chocarían.
        var tipoA = new TipoDocumento("Aptitud médica", 12, true, 90, AmbitoAplicacion.Trabajador);
        var tipoB = new TipoDocumento("Reconocimiento médico", 12, true, 91, AmbitoAplicacion.Trabajador);
        _dbContext.TiposDocumento.AddRange(tipoA, tipoB);
        await _dbContext.SaveChangesAsync();
        var docA = Documento.DeTrabajador(_trabajadorVisibleId, tipoA.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca);
        _dbContext.Documentos.Add(docA);
        await _dbContext.SaveChangesAsync();
        var docB = Documento.DeTrabajador(_trabajadorVisibleId, tipoB.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca);
        _dbContext.Documentos.Add(docB);
        await _dbContext.SaveChangesAsync();

        var alcance = new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorVisibleId]);
        var handler = new ObtenerDocumentoPorIdQueryHandler(
            _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, _dbContext, alcance);

        var a = await handler.Handle(new ObtenerDocumentoPorIdQuery(docA.Id), CancellationToken.None);
        var b = await handler.Handle(new ObtenerDocumentoPorIdQuery(docB.Id), CancellationToken.None);

        a!.NombreArchivoDescarga.Should().Contain("Aptitud medica").And.NotContain("_v2");
        b!.NombreArchivoDescarga.Should().Contain("Aptitud medica").And.EndWith("_v2.pdf");
    }
}
