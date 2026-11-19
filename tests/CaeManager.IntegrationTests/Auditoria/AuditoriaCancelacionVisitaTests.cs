using CaeManager.Domain.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Commands.CancelarVisita;
using CaeManager.Application.Visitas.Commands.ReactivarVisita;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// FS-11 (auditoría UX de flujos sin salida, 2026-09-24): cancelar y reactivar una
/// Visita quedan en la auditoría con el Actor real separado del Usuario simulado,
/// la fecha y el motivo. No hay código de auditoría específico: lo garantiza el
/// <see cref="AuditoriaInterceptor"/> genérico sobre las columnas de estado de la
/// Visita, y este test fija que no se escapan de él.
/// </summary>
public class AuditoriaCancelacionVisitaTests : IAsyncLifetime
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
    public async Task Cancelar_y_reactivar_dejan_actor_real_usuario_simulado_fecha_y_motivo()
    {
        var visitaId = await SembrarVisitaAsync();

        var gestor = Guid.NewGuid();
        Guid versionDeLaCancelacion;
        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(gestor))))
        {
            var resultado = await new CancelarVisitaCommandHandler(new VisitaRepository(contexto), contexto, new AlcanceDatosServiceFalso())
                .Handle(new CancelarVisitaCommand(visitaId, "Obra aplazada"), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
            versionDeLaCancelacion = resultado.Valor.VersionResultante;
        }

        // Reactivación bajo impersonación: el actor real no es el usuario simulado,
        // y la auditoría no puede atribuir la acción solo a este último.
        var actorReal = Guid.NewGuid();
        var simulado = Guid.NewGuid();
        var sesion = Guid.NewGuid();
        var antes = DateTime.UtcNow.AddSeconds(-5);
        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(
                         new ActorAuditoria(actorReal, simulado, TipoViaAcceso.SesionPrivilegiada, sesion))))
        {
            var resultado = await new ReactivarVisitaCommandHandler(
                    new VisitaRepository(contexto), contexto, new AlcanceDatosServiceFalso(), new EvaluadorExpedienteNulo(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ReactivarVisitaCommandHandler>.Instance)
                .Handle(new ReactivarVisitaCommand(visitaId, versionDeLaCancelacion, "Se retoma la obra"), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        }

        await using var lectura = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));
        var registros = await lectura.RegistrosAuditoria
            .Where(r => r.EntidadTipo == nameof(Visita) && r.EntidadId == visitaId && r.Accion == "Modificado")
            .OrderBy(r => r.FechaUtc)
            .ToListAsync();

        registros.Should().HaveCount(2, "una fila por la cancelación y otra por la reactivación");
        var (cancelacion, reactivacion) = (registros[0], registros[1]);

        cancelacion.ActorRealUsuarioId.Should().Be(gestor);
        cancelacion.DatosDespues.Should().Contain($"\"{nameof(Visita.EstaCancelada)}\":true")
            .And.Contain("Obra aplazada");

        reactivacion.ActorRealUsuarioId.Should().Be(actorReal, "el Actor real queda registrado aparte");
        reactivacion.UsuarioId.Should().Be(simulado, "y el Usuario simulado también");
        reactivacion.FechaUtc.Should().BeAfter(antes);
        reactivacion.DatosAntes.Should().Contain($"\"{nameof(Visita.EstaCancelada)}\":true")
            .And.Contain("Obra aplazada", "la reactivación deja constancia de la cancelación que deshace");
        reactivacion.DatosDespues.Should().Contain($"\"{nameof(Visita.EstaCancelada)}\":false")
            .And.Contain(nameof(Visita.ReactivadaEnUtc))
            .And.Contain("Se retoma la obra");
    }

    /// <summary>
    /// La foto del Trabajador en su asignación a una Visita (documento y nombre, histórico congelado de la exportación de
    /// extranjeros del mes) no llega en claro al historial: la anonimización del Trabajador no alcanzaría esa copia.
    /// </summary>
    [Fact]
    public async Task La_foto_del_Trabajador_en_la_asignacion_a_Visita_se_enmascara_en_la_auditoria()
    {
        var visitaId = await SembrarVisitaAsync();

        Guid asignacionId;
        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid()))))
        {
            var empresaId = await contexto.Empresas.Where(e => e.RazonSocial == "Contratista auditada").Select(e => e.Id).SingleAsync();
            var trabajador = CaeManager.Domain.Trabajadores.Trabajador.DeEmpresa(empresaId, "Amina", "El Idrissi", "AB1234567");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            var asignacion = new VisitaTrabajador(visitaId, trabajador);
            contexto.VisitasTrabajadores.Add(asignacion);
            await contexto.SaveChangesAsync();
            asignacionId = asignacion.Id;
        }

        await using var lectura = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));
        var alta = await lectura.RegistrosAuditoria
            .SingleAsync(r => r.EntidadTipo == nameof(VisitaTrabajador) && r.EntidadId == asignacionId);

        alta.DatosDespues.Should().NotContain("AB1234567").And.NotContain("Amina");
        alta.DatosDespues.Should().Contain($"\"{nameof(VisitaTrabajador.EsExtranjeroCobrable)}\":true",
            "la marca de la foto no es dato personal y queda en claro");
    }

    /// <summary>
    /// La instantánea del Trabajador en la credencial de acceso físico (AF-02: nombre y documento, de otro Tenant) no llega
    /// en claro al historial, ni al emitir ni al regenerar: Soporte TALVEG lee RegistrosAuditoria pero no CredencialesAcceso.
    /// </summary>
    [Fact]
    public async Task La_instantanea_del_Trabajador_en_la_credencial_de_acceso_se_enmascara_en_la_auditoria()
    {
        await SembrarVisitaAsync();
        var usuario = Guid.NewGuid();
        Guid credencialId;
        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(usuario))))
        {
            var credencial = CaeManager.Domain.Inbound.AccesoFisico.CredencialAcceso.Emitir(
                new CaeManager.Domain.Inbound.AccesoFisico.CredencialAcceso.Sujeto(Guid.NewGuid(), Guid.NewGuid(), "Ana Pérez", "12345678Z"),
                usuario, DateTime.UtcNow);
            contexto.CredencialesAcceso.Add(credencial);
            await contexto.SaveChangesAsync();
            credencialId = credencial.Id;
        }

        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(usuario))))
        {
            var credencial = await contexto.CredencialesAcceso.SingleAsync(c => c.Id == credencialId);
            credencial.Regenerar("Ana María Pérez", "87654321X", usuario, DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));
        var registros = await lectura.RegistrosAuditoria
            .Where(r => r.EntidadTipo == "CredencialAcceso" && r.EntidadId == credencialId).ToListAsync();

        registros.Should().HaveCount(2, "la emisión y la regeneración");
        foreach (var registro in registros)
        {
            var texto = (registro.DatosAntes ?? "") + (registro.DatosDespues ?? "");
            texto.Should().NotContain("Ana").And.NotContain("12345678Z").And.NotContain("87654321X");
        }
        registros.Single(r => r.Accion == "Modificado").DatosDespues.Should().Contain("\"Serie\":2",
            "la serie no es dato personal y queda en claro");
    }

    /// <summary>
    /// D-4 (#2005, contrato del 2026-11-15; revisor-puente y Codex r3 #2266): la reapertura del expediente por el Gestor
    /// CAE con cartera y el resello son dos registros de auditoría de una misma transacción, los dos con el Actor real
    /// separado del Usuario simulado. El «antes» de la reapertura conserva el sello retirado completo aunque el resello
    /// deje iguales la antelación nominal, la efectiva, el tramo y la atribución (aquí, «sin urgencia», que es también el
    /// valor sin sello); el resello lleva el sello nuevo.
    /// </summary>
    [Fact]
    public async Task Reabrir_el_expediente_deja_el_sello_anterior_completo_y_el_nuevo_con_actor_real_y_usuario_simulado()
    {
        var (visitaId, centroId, version) = await SembrarVisitaSelladaAsync();

        var actorReal = Guid.NewGuid();
        var simulado = Guid.NewGuid();
        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(
                         new ActorAuditoria(actorReal, simulado, TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid()))))
        {
            var repositorio = new VisitaRepository(contexto);
            var resultado = await HandlerDeReapertura(contexto, repositorio, centroId, new EvaluadorQueResella(repositorio, contexto))
                .Handle(new CaeManager.Application.Visitas.Commands.ReabrirExpedienteVisita.ReabrirExpedienteVisitaCommand(
                    visitaId, version, "Certificado de la mutua de otra empresa"), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        }

        await using var lectura = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));
        var visita = await lectura.Visitas.SingleAsync(v => v.Id == visitaId);
        visita.FechaHoraExpedienteCompletoUtc.Should().NotBe(SelloAnterior).And.NotBeNull("si sigue completa, se vuelve a sellar");
        visita.AntelacionNominalHoras.Should().Be(SelloDeLaVisita.NominalHoras, "el resello deja la misma antelación nominal");
        visita.AntelacionEfectivaHoras.Should().Be(SelloDeLaVisita.EfectivaHoras, "y la misma efectiva");

        var registros = await lectura.RegistrosAuditoria
            .Where(r => r.EntidadTipo == nameof(Visita) && r.EntidadId == visitaId && r.Accion == "Modificado"
                && r.ActorRealUsuarioId == actorReal)
            .ToListAsync();

        registros.Should().HaveCount(2, "la reapertura y el resello son dos guardados de la misma transacción");
        registros.Should().OnlyContain(r => r.UsuarioId == simulado, "el Usuario simulado queda aparte del Actor real");
        var reapertura = registros.Single(r => r.DatosDespues!.Contains("Certificado de la mutua de otra empresa"));
        var resello = registros.Single(r => r != reapertura);

        using (var antes = System.Text.Json.JsonDocument.Parse(reapertura.DatosAntes!))
        {
            var sello = antes.RootElement;
            sello.GetProperty(nameof(Visita.FechaHoraExpedienteCompletoUtc)).GetString().Should().StartWith("2026-11-02",
                "el sello anterior se conserva en el historial");
            sello.GetProperty(nameof(Visita.AntelacionNominalHoras)).GetDecimal().Should().Be(SelloDeLaVisita.NominalHoras);
            sello.GetProperty(nameof(Visita.AntelacionEfectivaHoras)).GetDecimal().Should().Be(SelloDeLaVisita.EfectivaHoras);
            sello.GetProperty(nameof(Visita.Tramo)).GetInt32().Should().Be((int)SelloDeLaVisita.Tramo);
            sello.GetProperty(nameof(Visita.Atribucion)).GetInt32().Should().Be((int)SelloDeLaVisita.Atribucion,
                "la atribución «sin urgencia» no cambia al retirar el sello y aun así se conserva");
        }
        reapertura.DatosDespues.Should().Contain(nameof(Visita.ExpedienteReabiertoEnUtc));

        using (var despues = System.Text.Json.JsonDocument.Parse(resello.DatosDespues!))
        {
            despues.RootElement.GetProperty(nameof(Visita.FechaHoraExpedienteCompletoUtc)).GetString().Should().NotBeNullOrEmpty();
            despues.RootElement.GetProperty(nameof(Visita.AntelacionNominalHoras)).GetDecimal().Should().Be(SelloDeLaVisita.NominalHoras);
        }
    }

    /// <summary>
    /// Codex r2 y r3 (#2266): si la reevaluación falla después de guardar la reapertura, la transacción la deshace. La
    /// Visita sigue sellada como estaba y no queda ningún registro de auditoría de la reapertura.
    /// </summary>
    [Fact]
    public async Task Si_la_reevaluacion_falla_la_reapertura_se_deshace_y_no_deja_registro()
    {
        var (visitaId, centroId, version) = await SembrarVisitaSelladaAsync();
        var actorReal = Guid.NewGuid();

        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(actorReal))))
        {
            var resultado = await HandlerDeReapertura(contexto, new VisitaRepository(contexto), centroId, new EvaluadorQueFalla())
                .Handle(new CaeManager.Application.Visitas.Commands.ReabrirExpedienteVisita.ReabrirExpedienteVisitaCommand(
                    visitaId, version, "motivo"), CancellationToken.None);
            resultado.Error.Codigo.Should().Be("Visita.ReaperturaNoEvaluada");
        }

        await using var lectura = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));
        var visita = await lectura.Visitas.SingleAsync(v => v.Id == visitaId);
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(SelloAnterior);
        visita.ExpedienteReabiertoEnUtc.Should().BeNull();
        (await lectura.RegistrosAuditoria.CountAsync(r => r.EntidadId == visitaId && r.ActorRealUsuarioId == actorReal))
            .Should().Be(0);
    }

    private static CaeManager.Application.Visitas.Commands.ReabrirExpedienteVisita.ReabrirExpedienteVisitaCommandHandler HandlerDeReapertura(
        CaeManagerDbContext contexto, VisitaRepository repositorio, Guid centroId,
        CaeManager.Application.Visitas.Antelacion.IEvaluadorExpedienteVisitaService evaluador) =>
        new(repositorio, contexto, new TransaccionDeComando(contexto), new AlcanceDatosServiceFalso(centroIds: [centroId]),
            new GestorCaeFalso(), evaluador,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CaeManager.Application.Visitas.Commands.ReabrirExpedienteVisita.ReabrirExpedienteVisitaCommandHandler>.Instance);

    private static readonly DateTime SelloAnterior = new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>El sello sembrado y el del resello: mismas antelaciones, tramo y atribución «sin urgencia».</summary>
    private static readonly ResultadoAntelacion SelloDeLaVisita =
        new(30m, 24m, 6m, TramoAntelacion.Estandar, AtribucionUrgencia.SinUrgencia);

    private async Task<(Guid VisitaId, Guid CentroId, Guid Version)> SembrarVisitaSelladaAsync()
    {
        await using var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid())));
        var titular = Empresa.CrearComoCliente("Titular auditado", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var proveedora = new Empresa("Contratista auditada", "B10380202");
        var centro = new Centro(titular.Id, proveedora.Id, "Almacén Sur");
        var hoy = DiaDeNegocio.Hoy();
        var visita = new Visita(centro.Id, hoy.AddDays(10), hoy.AddDays(11), null);
        visita.RegistrarOrigenSolicitud(Guid.NewGuid(), new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc));
        visita.MarcarExpedienteCompleto(SelloAnterior, SelloDeLaVisita);
        contexto.Empresas.AddRange(titular, proveedora);
        contexto.Centros.Add(centro);
        contexto.Visitas.Add(visita);
        await contexto.SaveChangesAsync();
        return (visita.Id, centro.Id, visita.Version);
    }

    private sealed class GestorCaeFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>
    /// Hace lo mismo que el evaluador real cuando el expediente sigue completo: lee la Visita por el repositorio (la
    /// instancia rastreada que la orden acaba de reabrir), la sella y guarda en el mismo contexto.
    /// </summary>
    private sealed class EvaluadorQueResella(VisitaRepository repositorio, CaeManagerDbContext contexto)
        : CaeManager.Application.Visitas.Antelacion.IEvaluadorExpedienteVisitaService
    {
        public async Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default)
        {
            var visita = await repositorio.ObtenerPorIdAsync(visitaId, cancellationToken);
            if (visita is null || visita.FechaHoraExpedienteCompletoUtc is not null) return false;
            if (!visita.MarcarExpedienteCompleto(DateTime.UtcNow, SelloDeLaVisita))
                return false;
            await contexto.SaveChangesAsync(cancellationToken);
            return true;
        }

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EvaluadorQueFalla : CaeManager.Application.Visitas.Antelacion.IEvaluadorExpedienteVisitaService
    {
        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("caída del evaluador");

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private async Task<Guid> SembrarVisitaAsync()
    {
        await using var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid())));
        var titular = Empresa.CrearComoCliente("Titular auditado", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var proveedora = new Empresa("Contratista auditada", "B10380202");
        var centro = new Centro(titular.Id, proveedora.Id, "Almacén Sur");
        var hoy = DiaDeNegocio.Hoy();
        var visita = new Visita(centro.Id, hoy.AddDays(2), hoy.AddDays(3), null);
        contexto.Empresas.AddRange(titular, proveedora);
        contexto.Centros.Add(centro);
        contexto.Visitas.Add(visita);
        await contexto.SaveChangesAsync();
        return visita.Id;
    }

    private CaeManagerDbContext CrearContexto(IActorAuditoria actor)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            // Mismo orden que en producción: auditoría primero, sellado después
            // (ver AuditoriaConIdentidadDualTests).
            .AddInterceptors(new AuditoriaInterceptor(actor), new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class EvaluadorExpedienteNulo : CaeManager.Application.Visitas.Antelacion.IEvaluadorExpedienteVisitaService
    {
        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
