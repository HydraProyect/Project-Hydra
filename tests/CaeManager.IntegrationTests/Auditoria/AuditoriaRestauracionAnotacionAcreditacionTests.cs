using CaeManager.Application.Documentos.Commands.MarcarAcreditacionAceptada;
using CaeManager.Application.Documentos.Commands.RestaurarAnotacionAcreditacion;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Application.Common;
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
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// Ficha 09: «Deshacer» una anotación de vigencia deja una fila de auditoría PROPIA
/// («Restaurado»), atómica con el cambio y con el Actor real separado del Usuario
/// simulado. La fija el <see cref="AuditoriaInterceptor"/> a petición de la entidad
/// (<see cref="IAccionAuditoriaPropia"/>); una marca que sobreviviera al guardado
/// etiquetaría como «Restaurado» una edición posterior.
/// </summary>
public class AuditoriaRestauracionAnotacionAcreditacionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Restaurar_deja_una_fila_Restaurado_con_actor_real_separado_del_simulado_y_la_marca_no_sobrevive()
    {
        var acreditacionId = await SembrarAcreditacionSubidaAsync();

        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid()))))
        {
            var a = await contexto.AcreditacionesDocumentoPlataforma.SingleAsync(x => x.Id == acreditacionId);
            a.MarcarAceptada(VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 3, 14)));
            await contexto.SaveChangesAsync();
        }

        var real = Guid.NewGuid();
        var simulado = Guid.NewGuid();
        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(
                         new ActorAuditoria(real, simulado, TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid()))))
        {
            var a = await contexto.AcreditacionesDocumentoPlataforma.SingleAsync(x => x.Id == acreditacionId);
            a.DeshacerAnotacion(EstadoAcreditacion.Subida, VigenciaEnPlataforma.SinConfirmar);
            await contexto.SaveChangesAsync();

            // Misma instancia, otro guardado: ya no es una restauración.
            a.ConfirmarVigencia(VigenciaEnPlataforma.NoVenceAqui);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));
        var filas = await lectura.RegistrosAuditoria
            .Where(r => r.EntidadTipo == nameof(AcreditacionDocumentoPlataforma) && r.EntidadId == acreditacionId)
            .OrderBy(r => r.FechaUtc).ToListAsync();

        var restaurado = filas.Should().ContainSingle(r => r.Accion == RegistroAuditoria.AccionRestaurado).Subject;
        restaurado.ActorRealUsuarioId.Should().Be(real);
        restaurado.UsuarioId.Should().Be(simulado, "el usuario simulado figura como autor, pero el Actor real queda aparte");
        restaurado.DatosAntes.Should().Contain($"\"{nameof(AcreditacionDocumentoPlataforma.EstadoVigencia)}\":{(int)EstadoVigenciaEnPlataforma.VenceEnFecha}");
        restaurado.DatosDespues.Should().Contain($"\"{nameof(AcreditacionDocumentoPlataforma.EstadoVigencia)}\":{(int)EstadoVigenciaEnPlataforma.SinConfirmar}");
        filas.Count(r => r.Accion == "Modificado").Should().BeGreaterThanOrEqualTo(2,
            "la anotación y la edición posterior siguen siendo «Modificado»");
    }

    [Fact]
    public async Task Una_restauracion_que_no_cambia_nada_no_deja_la_marca_viva_para_el_siguiente_guardado()
    {
        var acreditacionId = await SembrarAcreditacionSubidaAsync();

        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid()))))
        {
            var a = await contexto.AcreditacionesDocumentoPlataforma.SingleAsync(x => x.Id == acreditacionId);
            a.DeshacerAnotacion(EstadoAcreditacion.Subida, VigenciaEnPlataforma.SinConfirmar); // ya estaba así: Unchanged
            await contexto.SaveChangesAsync();

            a.ConfirmarVigencia(VigenciaEnPlataforma.NoVenceAqui);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));
        (await lectura.RegistrosAuditoria.CountAsync(r => r.EntidadId == acreditacionId && r.Accion == RegistroAuditoria.AccionRestaurado))
            .Should().Be(0, "una edición posterior es «Modificado», no «Restaurado»");
    }

    /// <summary>
    /// Cadena real anotar → recibo → deshacer con el handler, los interceptores de producción y DOS contextos
    /// (dos circuitos). El circuito A anota y conserva la entidad rastreada; el circuito B la rechaza después.
    /// Sin releer de la base de datos, A compararía contra su copia vieja, pasaría la versión y chocaría en el
    /// UPDATE con el mensaje genérico; con la lectura actualizada sale el mensaje accionable y nada se pisa.
    /// </summary>
    [Fact]
    public async Task Deshacer_tras_un_cambio_ajeno_en_otro_circuito_da_el_mensaje_accionable_y_no_pisa_nada()
    {
        var acreditacionId = await SembrarAcreditacionSubidaAsync();
        var actor = new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid()));

        await using var circuitoA = CrearContexto(actor);
        var repositorio = new AcreditacionDocumentoPlataformaRepository(circuitoA);
        var documentos = new DocumentoRepository(circuitoA);
        var alcance = new AlcanceDatosServiceFalso();

        var anotada = await new MarcarAcreditacionAceptadaCommandHandler(repositorio, documentos, alcance, circuitoA, circuitoA)
            .Handle(new MarcarAcreditacionAceptadaCommand(acreditacionId, VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 3, 14))), CancellationToken.None);
        anotada.EsExitoso.Should().BeTrue();
        var recibo = anotada.Valor;
        recibo.EstadoPrevio.Should().Be(EstadoAcreditacion.Subida);
        recibo.VigenciaPrevia.Should().Be(VigenciaEnPlataforma.SinConfirmar);

        await using (var circuitoB = CrearContexto(actor))
        {
            var ajena = await circuitoB.AcreditacionesDocumentoPlataforma.SingleAsync(x => x.Id == acreditacionId);
            ajena.Rechazar(CausaRechazoAcreditacion.Ilegible, "Firma ilegible", DateTime.UtcNow);
            await circuitoB.SaveChangesAsync();
        }

        var deshacer = await new RestaurarAnotacionAcreditacionCommandHandler(repositorio, documentos, alcance, circuitoA, circuitoA)
            .Handle(new RestaurarAnotacionAcreditacionCommand(acreditacionId, recibo.EstadoPrevio, recibo.VigenciaPrevia, recibo.VersionResultante),
                CancellationToken.None);

        deshacer.EsFallido.Should().BeTrue();
        deshacer.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto, "el conflicto sale del handler, no de una DbUpdateConcurrencyException");
        deshacer.Error.Mensaje.Should().Contain("a mano");

        await using var lectura = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));
        (await lectura.AcreditacionesDocumentoPlataforma.SingleAsync(x => x.Id == acreditacionId))
            .Estado.Should().Be(EstadoAcreditacion.Rechazada, "el rechazo ajeno sigue en pie");
    }

    private async Task<Guid> SembrarAcreditacionSubidaAsync()
    {
        await using var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid())));
        var titular = Empresa.CrearComoCliente("Titular auditado", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var proveedora = new Empresa("Contratista auditada", "B10380202");
        var centro = new Centro(titular.Id, proveedora.Id, "Almacén Sur");
        var proveedor = new ProveedorPlataformaCae($"prueba-{Guid.NewGuid():N}", "Portal de prueba");
        var acceso = CanalGestionDocumental.DePlataforma(centro.Id, "Portal", proveedor.Id, null, null, null);
        var tipo = new TipoDocumento("Formación PRL", 12, aplicaVencimientoAutomatico: true, orden: 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        var trabajador = Trabajador.DeEmpresa(proveedora.Id, "Ana", "Pérez", "12345678Z");
        var hoy = DiaDeNegocio.Hoy();
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(5)));
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, acceso.Id);
        acreditacion.MarcarSubida();
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
        return acreditacion.Id;
    }

    private CaeManagerDbContext CrearContexto(IActorAuditoria actor)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new AuditoriaInterceptor(actor), new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}
