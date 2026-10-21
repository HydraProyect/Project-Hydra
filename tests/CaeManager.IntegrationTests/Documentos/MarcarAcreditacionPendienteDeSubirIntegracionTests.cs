using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionPendienteDeSubir;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// «Pendiente de subir» del panel «Gestionar acreditación en plataforma», con PostgreSQL y los interceptores de producción:
/// cambia solo la acreditación (el Documento en TALVEG queda igual), deja su fila de auditoría y, fuera de la cartera de quien
/// lo pide, no escribe nada.
/// </summary>
public class MarcarAcreditacionPendienteDeSubirIntegracionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Una_validada_vuelve_a_pendiente_de_subir_sin_tocar_el_documento_y_queda_auditada()
    {
        var (acreditacionId, documentoId) = await SembrarAcreditacionValidadaAsync();

        await using (var contexto = CrearContexto())
        {
            var resultado = await Handler(contexto, new AlcanceDatosServiceFalso())
                .Handle(new MarcarAcreditacionPendienteDeSubirCommand(acreditacionId), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue();
        }

        await using var lectura = CrearContexto();
        var acreditacion = await lectura.AcreditacionesDocumentoPlataforma.SingleAsync(a => a.Id == acreditacionId);
        acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        acreditacion.EstadoVigencia.Should().Be(EstadoVigenciaEnPlataforma.SinConfirmar);
        (await lectura.RegistrosAuditoria.CountAsync(r => r.EntidadId == documentoId && r.Accion == "Modificado"))
            .Should().Be(0, "el Documento en TALVEG no se toca");
        (await lectura.RegistrosAuditoria.CountAsync(r =>
                r.EntidadTipo == nameof(AcreditacionDocumentoPlataforma) && r.EntidadId == acreditacionId && r.Accion == "Modificado"))
            .Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Fuera_de_la_cartera_no_escribe_y_responde_como_si_no_existiera()
    {
        var (acreditacionId, _) = await SembrarAcreditacionValidadaAsync();
        var fueraDeCartera = new AlcanceDatosServiceFalso(clienteIds: [], centroIds: [], empresaIds: [], subcontrataIds: [], trabajadorIds: [], vehiculoIds: []);

        await using (var contexto = CrearContexto())
        {
            var resultado = await Handler(contexto, fueraDeCartera)
                .Handle(new MarcarAcreditacionPendienteDeSubirCommand(acreditacionId), CancellationToken.None);
            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        }

        await using var lectura = CrearContexto();
        (await lectura.AcreditacionesDocumentoPlataforma.SingleAsync(a => a.Id == acreditacionId))
            .Estado.Should().Be(EstadoAcreditacion.Aceptada);
    }

    private static MarcarAcreditacionPendienteDeSubirCommandHandler Handler(CaeManagerDbContext contexto, IAlcanceDatosService alcance) =>
        new(new AcreditacionDocumentoPlataformaRepository(contexto), new DocumentoRepository(contexto), alcance, contexto, contexto);

    private async Task<(Guid AcreditacionId, Guid DocumentoId)> SembrarAcreditacionValidadaAsync()
    {
        await using var contexto = CrearContexto();
        var titular = Empresa.CrearComoCliente("Titular de prueba", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var proveedora = new Empresa("Contratista de prueba", "B10380202");
        var centro = new Centro(titular.Id, proveedora.Id, "Almacén Sur");
        var proveedor = new ProveedorPlataformaCae($"prueba-{Guid.NewGuid():N}", "Portal de prueba");
        var acceso = CanalGestionDocumental.DePlataforma(centro.Id, "Portal", proveedor.Id, null, null, null);
        var tipo = new TipoDocumento("Formación PRL", 12, aplicaVencimientoAutomatico: true, orden: 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        var trabajador = Trabajador.DeEmpresa(proveedora.Id, "Ana", "Pérez", "12345678Z");
        var hoy = DiaDeNegocio.Hoy();
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(5)));
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, acceso.Id);
        acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(hoy.AddYears(1)));
        contexto.Empresas.AddRange(titular, proveedora);
        contexto.Centros.Add(centro);
        contexto.ProveedoresPlataformaCae.Add(proveedor);
        contexto.CanalesGestionDocumental.Add(acceso);
        contexto.TiposDocumento.Add(tipo);
        contexto.Trabajadores.Add(trabajador);
        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, hoy.AddDays(-1)));
        contexto.Documentos.Add(documento);
        contexto.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await contexto.SaveChangesAsync();
        return (acreditacion.Id, documento.Id);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new AuditoriaInterceptor(new ActorFalso()), new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ActorFalso : IActorAuditoria
    {
        private readonly ActorAuditoria _actor = ActorAuditoria.Normal(Guid.NewGuid());
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(_actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => _actor;
    }
}
