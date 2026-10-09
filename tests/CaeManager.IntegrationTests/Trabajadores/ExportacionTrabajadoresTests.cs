using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Web.Features.Trabajadores;
using ClosedXML.Excel;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Trabajadores;

/// <summary>
/// <c>/trabajadores/exportar.xlsx</c> con la consulta REAL del listado contra
/// PostgreSQL: «Exportar esta vista» devuelve el subconjunto que selecciona el
/// criterio, ninguna de las dos variantes saca un Trabajador de otro Tenant, y la
/// descarga —que lleva DNI— deja una fila en la auditoría del Tenant propietario
/// con el número de filas y los criterios, sin los datos exportados.
///
/// <para>
/// Límite del instrumento: llama a <see cref="TrabajadoresEndpoints.ExportarAsync"/>
/// directamente, sin el routing ni la autorización de ASP.NET (eso lo miden los E2E
/// de exportación), y el aislamiento que observa es el filtro de Tenant del
/// <c>DbContext</c>, no RLS.
/// </para>
/// </summary>
public class ExportacionTrabajadoresTests : IAsyncLifetime
{
    private const string DniLucia = "12345678Z";
    private const string DniIker = "00000000T";
    private const string DniOtroTenant = "00000001R";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using (var contexto = CrearContexto(_tenant))
        {
            await contexto.Database.MigrateAsync();

            // El estado documental de cada fila se calcula con los umbrales del Tenant.
            if (!await contexto.ParametrosSistema.AnyAsync())
                contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

            var empresa = new Empresa("Alfa Montajes S.L.", "B12345674");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            contexto.Trabajadores.AddRange(
                Trabajador.DeEmpresa(empresa.Id, "Lucía", "Prieto Ramos", DniLucia),
                Trabajador.DeEmpresa(empresa.Id, "Iker", "Sanz Olmo", DniIker));
            await contexto.SaveChangesAsync();
        }

        // Mismo apellido que Lucía: si el filtro de Tenant no actuara, la búsqueda
        // «Prieto» lo traería.
        await using (var contexto = CrearContexto(_otroTenant))
        {
            if (!await contexto.ParametrosSistema.AnyAsync())
                contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

            var empresa = new Empresa("Beta Servicios S.L.", "B87654331");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            contexto.Trabajadores.Add(Trabajador.DeEmpresa(empresa.Id, "Marta", "Prieto Vega", DniOtroTenant));
            await contexto.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Exportar_todo_lleva_los_Trabajadores_del_Tenant_con_su_DNI_y_ninguno_de_otro()
    {
        var filas = await ExportarAsync(_tenant);

        filas.Select(f => f[2]).Should().BeEquivalentTo([DniLucia, DniIker]);
        filas.SelectMany(f => f).Should().NotContain(DniOtroTenant).And.NotContain("Prieto Vega");
    }

    [Fact]
    public async Task Exportar_esta_vista_lleva_solo_lo_que_selecciona_la_busqueda()
    {
        var filas = await ExportarAsync(_tenant, q: "Prieto");

        // Control positivo del dato: sin filtro hay dos, y en la base hay dos «Prieto».
        (await ExportarAsync(_tenant)).Should().HaveCount(2);
        filas.Should().ContainSingle().Which.Should().StartWith(["Prieto Ramos", "Lucía", DniLucia]);
    }

    [Fact]
    public async Task El_otro_Tenant_exporta_lo_suyo_y_no_lo_de_este()
    {
        var filas = await ExportarAsync(_otroTenant, q: "Prieto");

        filas.Should().ContainSingle().Which[2].Should().Be(DniOtroTenant);
    }

    [Fact]
    public async Task La_descarga_deja_una_fila_en_la_auditoria_del_Tenant_sin_los_datos()
    {
        // Se busca por DNI, que es lo que el buscador del listado admite.
        (await ExportarAsync(_tenant, q: DniLucia)).Should().ContainSingle();

        var registro = (await ExportacionesAsync(_tenant)).Should().ContainSingle().Subject;
        registro.EntidadTipo.Should().Be(nameof(Trabajador));
        registro.TenantId.Should().Be(_tenant, "la fila va al Tenant propietario de los datos exportados");
        registro.UsuarioId.Should().Be(_usuario);
        registro.ActorRealUsuarioId.Should().Be(_usuario);
        registro.DatosDespues.Should().Contain("\"filas\":1").And.Contain("\"busqueda\"");
        registro.DatosDespues.Should().NotContain(DniLucia, "el rastro dice qué salió, no lo copia: tampoco la búsqueda");

        (await ExportacionesAsync(_otroTenant)).Should().BeEmpty();
    }

    private async Task<List<List<string>>> ExportarAsync(Guid tenant, string? q = null)
    {
        await using var contexto = CrearContexto(tenant);
        var handler = new ObtenerTrabajadoresQueryHandler(
            contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso(),
            new CalculoEstadoDocumentalService(contexto, contexto));
        // Rastro REAL, contra la misma base: lo que se mide es la fila que queda.
        var registro = new RegistroExportacionService(
            new ActorFijo(ActorAuditoria.Normal(_usuario)),
            new RegistroAccesoDatoSensibleRepository(contexto, NullLogger<RegistroAccesoDatoSensibleRepository>.Instance));

        var resultado = await TrabajadoresEndpoints.ExportarAsync(
            new MediadorDelListado(handler, tenant), new TenantActualAmbiental { TenantId = tenant }, registro, default, q: q);

        await using var stream = resultado.Should().BeOfType<FileStreamHttpResult>().Subject.FileStream;
        using var libro = new XLWorkbook(stream);
        return libro.Worksheet("Trabajadores").RangeUsed()!.Rows().Skip(1)
            .Select(fila => fila.Cells().Select(c => c.GetString()).ToList())
            .ToList();
    }

    private async Task<List<RegistroAuditoria>> ExportacionesAsync(Guid tenant)
    {
        await using var contexto = CrearContexto(tenant);
        return await contexto.RegistrosAuditoria
            .Where(r => r.Accion == RegistroAuditoria.AccionExportacion)
            .ToListAsync();
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    /// <summary>El handler real del listado detrás de <see cref="IMediator"/>; sin cartera, nadie tiene que elegir empresa.</summary>
    private sealed class MediadorDelListado(ObtenerTrabajadoresQueryHandler handler, Guid tenant) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                ObtenerClientesAutorizadosQuery => (IReadOnlyList<ClienteAutorizadoDto>)
                    [new ClienteAutorizadoDto(tenant, "Tenant propietario", EsOrigen: true, EsGestionadoPorOperacion: false)],
                ObtenerTrabajadoresQuery consulta => await handler.Handle(consulta, cancellationToken),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return (TResponse)respuesta;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
