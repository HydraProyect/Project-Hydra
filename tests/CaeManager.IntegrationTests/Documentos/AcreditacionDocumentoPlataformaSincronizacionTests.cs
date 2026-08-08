using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (b)/Lote 2-D: crear/renovar
/// un Documento sincroniza sus AcreditacionDocumentoPlataforma contra los
/// accesos de plataforma que hoy le aplican.
/// </summary>
public class AcreditacionDocumentoPlataformaSincronizacionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Crear_documento_de_trabajador_genera_una_acreditacion_por_cada_acceso_de_plataforma_de_su_centro()
    {
        Guid trabajadorId, tipoDocumentoId, canalId;
        await using (var contexto = CrearContexto())
        {
            var cliente = new Cliente("Cliente Acreditación S.L.", "B10380194", esCritico: false);
            var empresa = new Empresa("Empresa Acreditación S.L.", "B10380186");
            contexto.Clientes.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro Acreditación");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
            var canalPlataforma = CanalGestionDocumental.DePlataforma(
                centro.Id, "Gestión general", proveedor.Id, "https://plataforma.test", "usuario", "clave");
            var canalEmail = CanalGestionDocumental.PorEmail(centro.Id, "Contacto alterno", "contacto@centro.test", "Contacto");
            contexto.CanalesGestionDocumental.AddRange(canalPlataforma, canalEmail);

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "Gómez", "12345678Z");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, new DateOnly(2026, 1, 1)));
            var tipoDocumento = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            trabajadorId = trabajador.Id;
            tipoDocumentoId = tipoDocumento.Id;
            canalId = canalPlataforma.Id;
        }

        Guid documentoId;
        await using (var contexto = CrearContexto())
        {
            var handler = ConstruirHandlerCrear(contexto);

            var resultado = await handler.Handle(
                new CrearDocumentoCommand(
                    TrabajadorId: trabajadorId, ClienteId: null, EmpresaId: null, VehiculoId: null, ProyectoId: null,
                    TipoDocumentoId: tipoDocumentoId, FechaEmision: new DateOnly(2026, 1, 1),
                    FechaVencimientoManual: null, ArchivoUrl: null, Comentarios: null),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
            documentoId = resultado.Valor;
        }

        await using (var verificacion = CrearContexto())
        {
            var acreditaciones = await verificacion.AcreditacionesDocumentoPlataforma
                .Where(a => a.DocumentoId == documentoId)
                .ToListAsync();

            acreditaciones.Should().ContainSingle("solo el canal de tipo Plataforma acredita, el de tipo Email no");
            acreditaciones[0].CanalGestionDocumentalId.Should().Be(canalId);
            acreditaciones[0].Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        }
    }

    [Fact]
    public async Task Crear_documento_de_trabajador_sin_asignaciones_activas_no_genera_ninguna_acreditacion()
    {
        Guid trabajadorId, tipoDocumentoId;
        await using (var contexto = CrearContexto())
        {
            var empresa = new Empresa("Empresa Sin Centro S.L.", "B10380186");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Luis", "Pérez", "87654321X");
            var tipoDocumento = new TipoDocumento("Formación", 12, true, 1, AmbitoAplicacion.Trabajador);
            contexto.Trabajadores.Add(trabajador);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            trabajadorId = trabajador.Id;
            tipoDocumentoId = tipoDocumento.Id;
        }

        Guid documentoId;
        await using (var contexto = CrearContexto())
        {
            var handler = ConstruirHandlerCrear(contexto);
            var resultado = await handler.Handle(
                new CrearDocumentoCommand(
                    TrabajadorId: trabajadorId, ClienteId: null, EmpresaId: null, VehiculoId: null, ProyectoId: null,
                    TipoDocumentoId: tipoDocumentoId, FechaEmision: new DateOnly(2026, 1, 1),
                    FechaVencimientoManual: null, ArchivoUrl: null, Comentarios: null),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
            documentoId = resultado.Valor;
        }

        await using var verificacion = CrearContexto();
        (await verificacion.AcreditacionesDocumentoPlataforma.Where(a => a.DocumentoId == documentoId).AnyAsync())
            .Should().BeFalse();
    }

    [Fact]
    public async Task Renovar_documento_reinicia_sus_acreditaciones_a_pendiente_de_subir_sin_tocar_el_historial()
    {
        Guid trabajadorId, tipoDocumentoId;
        await using (var contexto = CrearContexto())
        {
            var cliente = new Cliente("Cliente Renovación S.L.", "B10380194", esCritico: false);
            var empresa = new Empresa("Empresa Renovación S.L.", "B10380186");
            contexto.Clientes.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro Renovación");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
            var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Gestión general", proveedor.Id, null, null, null);
            contexto.CanalesGestionDocumental.Add(canal);

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Marta", "Ruiz", "11223344B");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, new DateOnly(2026, 1, 1)));
            var tipoDocumento = new TipoDocumento("Certificado", 12, true, 1, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            trabajadorId = trabajador.Id;
            tipoDocumentoId = tipoDocumento.Id;
        }

        Guid documentoId;
        await using (var contexto = CrearContexto())
        {
            var handler = ConstruirHandlerCrear(contexto);
            var resultado = await handler.Handle(
                new CrearDocumentoCommand(
                    TrabajadorId: trabajadorId, ClienteId: null, EmpresaId: null, VehiculoId: null, ProyectoId: null,
                    TipoDocumentoId: tipoDocumentoId, FechaEmision: new DateOnly(2026, 1, 1),
                    FechaVencimientoManual: null, ArchivoUrl: null, Comentarios: null),
                CancellationToken.None);
            documentoId = resultado.Valor;
        }

        // El gestor marca la acreditación como rechazada antes de renovar el documento.
        await using (var contexto = CrearContexto())
        {
            var acreditacionRepo = new AcreditacionDocumentoPlataformaRepository(contexto);
            var acreditacion = (await acreditacionRepo.ObtenerPorDocumentoIdAsync(documentoId)).Single();
            acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Escaneo borroso.", DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var handlerRenovar = new RenovarDocumentoCommandHandler(
                new DocumentoRepository(contexto), contexto, new AlcanceDatosServiceFalso(), contexto,
                new ColaAnalisisDocumentoFalsa(), new CurrentUserServiceFalso(),
                new AcreditacionDocumentoPlataformaRepository(contexto), contexto);

            var resultado = await handlerRenovar.Handle(
                new RenovarDocumentoCommand(documentoId, new DateOnly(2026, 2, 1), null, null, null),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var verificacion = CrearContexto();
        var acreditacionFinal = await verificacion.AcreditacionesDocumentoPlataforma
            .Include(a => a.HistorialRechazos)
            .SingleAsync(a => a.DocumentoId == documentoId);

        acreditacionFinal.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        acreditacionFinal.HistorialRechazos.Should().ContainSingle("el historial de un rechazo real anterior no se borra al renovar");
    }

    private static CrearDocumentoCommandHandler ConstruirHandlerCrear(CaeManagerDbContext contexto) =>
        new(
            new DocumentoRepository(contexto), contexto, contexto, contexto, contexto, contexto, contexto,
            contexto, new ColaAnalisisDocumentoFalsa(), new CurrentUserServiceFalso(),
            new DerivarCanalesAplicablesDocumentoService(contexto, contexto, contexto),
            new AcreditacionDocumentoPlataformaRepository(contexto));

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }

    private sealed class ColaAnalisisDocumentoFalsa : ITrabajoAnalisisDocumentoRepository
    {
        public void Agregar(TrabajoAnalisisDocumento trabajo)
        {
        }

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TrabajoAnalisisDocumento?>(null);

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(
            TimeSpan umbral, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrabajoAnalisisDocumento>>([]);
    }
}
