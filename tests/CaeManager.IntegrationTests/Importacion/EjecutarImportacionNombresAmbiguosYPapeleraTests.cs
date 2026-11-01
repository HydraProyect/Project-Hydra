using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// C-06 y C-07 contra PostgreSQL real: lo que un doble no reproduce. <c>IX_Trabajadores_TenantId_Dni</c> incluye a los
/// Trabajadores de la Papelera, así que un alta con su DNI hacía fallar el guardado entero con un 23505 sin traducir
/// (0 filas de N); y dos Centros homónimos (o dos Tipos de documento que solo difieren en mayúsculas, que el índice
/// único sí distingue) tumbaban el handler con una <see cref="ArgumentException"/> de diccionario antes de escribir.
/// Conforme al éxito parcial de DCR-12 B, la fila afectada queda en <c>Omitidos</c> y el resto se escribe.
/// </summary>
public class EjecutarImportacionNombresAmbiguosYPapeleraTests : IAsyncLifetime
{
    private const string NombreEmpresa = "Empresa C-06 S.L.";
    private const string DniEnPapelera = "12345678Z";
    private const string DniActivo = "00000000T";
    private const string CentroHomonimo = "Planta Norte";
    private const string TipoUnico = "Reconocimiento médico";
    private const string DniEnPapeleraDeOtroTenant = "11111111H";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var empresa = new Empresa(NombreEmpresa);
        contexto.Empresas.Add(empresa);

        var enPapelera = Trabajador.DeEmpresa(empresa.Id, "Pablo", "Eliminado", DniEnPapelera);
        enPapelera.MarcarComoEliminado(Guid.NewGuid());
        contexto.Trabajadores.Add(enPapelera);
        contexto.Trabajadores.Add(Trabajador.DeEmpresa(empresa.Id, "Marta", "Ruiz", DniActivo));

        var clienteUno = Empresa.CrearComoCliente("Cliente Uno S.A.", "B12345674", false, notas: null, ejecutivoUsuarioId: null);
        var clienteDos = Empresa.CrearComoCliente("Cliente Dos S.A.", "P1234567D", false, notas: null, ejecutivoUsuarioId: null);
        contexto.Empresas.AddRange(clienteUno, clienteDos);
        contexto.Centros.Add(new Centro(clienteUno.Id, empresa.Id, CentroHomonimo));
        contexto.Centros.Add(new Centro(clienteDos.Id, empresa.Id, CentroHomonimo));

        contexto.TiposDocumento.Add(new TipoDocumento("Formación 20h", 12, true, orden: 1, AmbitoAplicacion.Trabajador));
        contexto.TiposDocumento.Add(new TipoDocumento("Formación 20H", 12, true, orden: 2, AmbitoAplicacion.Trabajador));
        contexto.TiposDocumento.Add(new TipoDocumento(TipoUnico, 12, true, orden: 3, AmbitoAplicacion.Trabajador));
        await contexto.SaveChangesAsync();

        // Otro Tenant propietario, con un Trabajador en SU Papelera: su DNI no puede afectar a la importación de este.
        var otroTenant = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        await using var contextoOtroTenant = CrearContexto(otroTenant);
        var empresaOtroTenant = new Empresa("Empresa de otro Tenant S.L.");
        contextoOtroTenant.Empresas.Add(empresaOtroTenant);
        var eliminadoOtroTenant = Trabajador.DeEmpresa(empresaOtroTenant.Id, "Ana", "Ajena", DniEnPapeleraDeOtroTenant);
        eliminadoOtroTenant.MarcarComoEliminado(Guid.NewGuid());
        contextoOtroTenant.Trabajadores.Add(eliminadoOtroTenant);
        await contextoOtroTenant.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_DNI_en_la_Papelera_omite_solo_su_fila_y_el_resto_del_archivo_se_guarda()
    {
        var plan = Plan(trabajadores:
        [
            new TrabajadorImportadoDto(NombreEmpresa, "Pablo", "Nuevo", DniEnPapelera, null, null, YaExiste: false),
            new TrabajadorImportadoDto(NombreEmpresa, "Luis", "Pérez", "99999999R", null, null, YaExiste: false),
        ]);

        await using var contexto = CrearContexto();
        var resultado = await ConstruirHandler(contexto).Handle(new EjecutarImportacionCommand(plan), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("el 23505 del índice (TenantId, Dni) ya no llega a producirse");
        resultado.Valor.TrabajadoresCreados.Should().Be(1);
        resultado.Valor.Omitidos.Should().ContainSingle(o => o.Descripcion.Contains(DniEnPapelera))
            .Which.Motivo.Should().Contain("Papelera").And.Contain("restáuralo");

        await using var verificacion = CrearContexto();
        (await verificacion.Trabajadores.IgnoreQueryFilters().CountAsync(t => t.Dni == DniEnPapelera)).Should().Be(1,
            "el Trabajador de la Papelera sigue siendo el único con ese DNI");
        (await verificacion.Trabajadores.CountAsync(t => t.Dni == "99999999R")).Should().Be(1);
    }

    [Fact]
    public async Task Un_DNI_en_la_Papelera_de_otro_Tenant_no_afecta_a_la_importacion_ni_se_revela()
    {
        var plan = Plan(trabajadores:
            [new TrabajadorImportadoDto(NombreEmpresa, "Luis", "Pérez", DniEnPapeleraDeOtroTenant, null, null, YaExiste: false)]);

        await using var contexto = CrearContexto();
        var resultado = await ConstruirHandler(contexto).Handle(new EjecutarImportacionCommand(plan), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.TrabajadoresCreados.Should().Be(1, "el DNI de la Papelera de otro Tenant no existe para este");
        resultado.Valor.Omitidos.Should().BeEmpty("ni siquiera se insinúa que ese DNI esté en la Papelera de otro Tenant");
    }

    [Fact]
    public async Task Sin_Tenant_propietario_resuelto_la_consulta_de_Papelera_no_devuelve_ningun_DNI()
    {
        await using var contexto = CrearContexto(new TenantActualAmbiental { TenantId = null });

        var enPapelera = await new TrabajadorRepository(contexto).ObtenerDnisEnPapeleraAsync(
            [DniEnPapelera, DniEnPapeleraDeOtroTenant]);

        enPapelera.Should().BeEmpty("sin Tenant propietario el predicado explícito falla cerrado: IgnoreQueryFilters no abre nada");
    }

    [Fact]
    public async Task La_consulta_de_Papelera_no_devuelve_el_DNI_de_un_Trabajador_activo()
    {
        await using var contexto = CrearContexto();

        var enPapelera = await new TrabajadorRepository(contexto).ObtenerDnisEnPapeleraAsync([DniActivo, DniEnPapelera]);

        enPapelera.Should().BeEquivalentTo([DniEnPapelera], "el activo no está en la Papelera");
    }

    [Fact]
    public async Task Centros_homonimos_y_Tipos_de_documento_que_difieren_en_mayusculas_omiten_solo_las_filas_ambiguas()
    {
        var plan = Plan(
            documentos:
            [
                new DocumentoImportadoDto(DniActivo, "Formación 20h", new DateOnly(2026, 2, 1), YaExiste: false),
                new DocumentoImportadoDto(DniActivo, TipoUnico, new DateOnly(2026, 2, 1), YaExiste: false),
            ],
            asignaciones: [new AsignacionImportadaDto(DniActivo, CentroHomonimo, YaExiste: false)]);

        await using var contexto = CrearContexto();
        var resultado = await ConstruirHandler(contexto).Handle(new EjecutarImportacionCommand(plan), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("un dato legítimo no puede inutilizar el Importador");
        resultado.Valor.DocumentosCreados.Should().Be(1);
        resultado.Valor.AsignacionesCreadas.Should().Be(0);
        resultado.Valor.Omitidos.Should().HaveCount(2);
        resultado.Valor.Omitidos.Should().Contain(o => o.Motivo.Contains("varios tipos de documento"));
        resultado.Valor.Omitidos.Should().Contain(o => o.Motivo.Contains("Hay varios centros"));

        await using var verificacion = CrearContexto();
        (await verificacion.Documentos.CountAsync()).Should().Be(1);
        (await verificacion.Asignaciones.CountAsync()).Should().Be(0);
    }

    private static PlanImportacionDto Plan(
        IReadOnlyList<TrabajadorImportadoDto>? trabajadores = null,
        IReadOnlyList<DocumentoImportadoDto>? documentos = null,
        IReadOnlyList<AsignacionImportadaDto>? asignaciones = null) =>
        new(Guid.NewGuid(), [], [], trabajadores ?? [], documentos ?? [], asignaciones ?? [], [], []);

    private static EjecutarImportacionCommandHandler ConstruirHandler(CaeManagerDbContext contexto) =>
        new(
            new EmpresaRepository(contexto), new TrabajadorRepository(contexto), new DocumentoRepository(contexto),
            new AsignacionRepository(contexto), new OperacionImportacionRepository(contexto),
            contexto, contexto, contexto, contexto, contexto, contexto,
            new AutorizacionEscrituraEfectivaFalsa(), AltaAcreditacionesDePrueba.Con(contexto));

    private CaeManagerDbContext CrearContexto(TenantActualAmbiental? tenant = null)
    {
        tenant ??= _tenantActual;
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql =>
            {
                npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");
                npgsql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(30), errorCodesToAdd: null);
            })
            .AddInterceptors(new TenantSelladoInterceptor(tenant))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenant);
    }
}
