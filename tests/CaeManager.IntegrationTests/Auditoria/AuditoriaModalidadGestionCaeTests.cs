using CaeManager.Application.Centros.Commands.EstablecerGestionCaeCentro;
using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// P1-X2 (condición de la coordinadora, 2026-09-25): marcar un Centro de
/// Trabajo sin gestión CAE silencia todo su cumplimiento, así que el cambio de
/// modalidad tiene que quedar en la auditoría con el Actor real, el valor
/// anterior y el nuevo. No hay código de auditoría específico: lo garantiza el
/// <see cref="AuditoriaInterceptor"/> genérico, y este test fija que la
/// columna nueva no se escapa de él (una exclusión futura de Centro o de la
/// propiedad lo pondría en rojo).
/// </summary>
public class AuditoriaModalidadGestionCaeTests : IAsyncLifetime
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
    public async Task Marcar_un_centro_sin_gestion_cae_deja_actor_real_valor_anterior_y_valor_nuevo()
    {
        Guid centroId;
        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid()))))
        {
            var titular = Empresa.CrearComoCliente("Titular auditado", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            var proveedora = new Empresa("Contratista auditada", "B10380202");
            var centro = new Centro(titular.Id, proveedora.Id, "Almacén Sur");
            contexto.Empresas.AddRange(titular, proveedora);
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();
            centroId = centro.Id;
        }

        var gestor = Guid.NewGuid();
        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(gestor))))
        {
            var centro = await contexto.Centros.SingleAsync(c => c.Id == centroId);
            centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));
        var registro = await lectura.RegistrosAuditoria
            .Where(r => r.EntidadTipo == nameof(Centro) && r.EntidadId == centroId && r.Accion == "Modificado")
            .SingleAsync();

        registro.ActorRealUsuarioId.Should().Be(gestor);
        registro.DatosAntes.Should().Contain($"\"{nameof(Centro.GestionCae)}\":{(int)ModalidadGestionCae.ConGestionCae}");
        registro.DatosDespues.Should().Contain($"\"{nameof(Centro.GestionCae)}\":{(int)ModalidadGestionCae.SinGestionCae}");
    }

    /// <summary>
    /// P1-X2 con P0-7: mientras el Centro no exige nada no nacen acreditaciones
    /// de plataforma; al volver a gestión CAE nacen en el mismo guardado. La base
    /// todavía tiene el Centro como sin gestión cuando el servicio de altas
    /// consulta, así que solo Postgres real distingue si el handler lo declara.
    /// </summary>
    [Fact]
    public async Task Volver_a_gestion_cae_da_de_alta_las_acreditaciones_pendientes_en_el_mismo_guardado()
    {
        Guid centroId, documentoId, accesoId;
        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid()))))
        {
            var titular = Empresa.CrearComoCliente("Titular reconciliado", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            var proveedora = new Empresa("Contratista reconciliada", "B10380202");
            var centro = new Centro(titular.Id, proveedora.Id, "Almacén Sur");
            centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
            var proveedor = new ProveedorPlataformaCae($"prueba-{Guid.NewGuid():N}", "Portal de prueba");
            var acceso = CanalGestionDocumental.DePlataforma(centro.Id, "Portal", proveedor.Id, null, null, null);
            var tipo = new TipoDocumento("Formación PRL", 12, aplicaVencimientoAutomatico: true, orden: 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
            var trabajador = Trabajador.DeEmpresa(proveedora.Id, "Ana", "Pérez", "12345678Z");
            var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
            var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(5)));
            contexto.Empresas.AddRange(titular, proveedora);
            contexto.Centros.Add(centro);
            contexto.ProveedoresPlataformaCae.Add(proveedor);
            contexto.CanalesGestionDocumental.Add(acceso);
            contexto.TiposDocumento.Add(tipo);
            contexto.Trabajadores.Add(trabajador);
            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, hoy.AddDays(-1)));
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();
            (centroId, documentoId, accesoId) = (centro.Id, documento.Id, acceso.Id);
        }

        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid()))))
        {
            var resultado = await new EstablecerGestionCaeCentroCommandHandler(
                    new CentroRepository(contexto), contexto, AltaAcreditacionesDePrueba.Con(contexto),
                    new AlcanceDatosServiceFalso(), contexto)
                .Handle(new EstablecerGestionCaeCentroCommand(centroId, ModalidadGestionCae.ConGestionCae), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue();
        }

        await using var lectura = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));
        (await lectura.Centros.SingleAsync(c => c.Id == centroId)).GestionCae.Should().Be(ModalidadGestionCae.ConGestionCae);
        (await lectura.AcreditacionesDocumentoPlataforma
                .Select(a => new { a.DocumentoId, a.CanalGestionDocumentalId, a.Estado })
                .ToListAsync())
            .Should().ContainSingle(a => a.DocumentoId == documentoId && a.CanalGestionDocumentalId == accesoId
                && a.Estado == EstadoAcreditacion.PendienteDeSubir);
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
}
