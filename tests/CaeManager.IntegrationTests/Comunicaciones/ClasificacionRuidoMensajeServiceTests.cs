using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Verifica el join real Postgres (ReclamacionDocumentalDocumento → Documento) que sustenta el
/// patrón "repetición de un pendiente ya reclamado formalmente" (ronda de reducción de ruido en
/// Comunicaciones) — la lógica de decisión pura ya está cubierta en Application.Tests, esto
/// confirma que la traducción de EF Core (Join/Contains) coincide.
/// </summary>
public class ClasificacionRuidoMensajeServiceTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clienteId;
    private Guid _trabajadorReclamadoId;
    private Guid _trabajadorSinReclamarId;
    private Guid _tipoDocumentoId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = new Cliente("Cliente Ruido Reclamado S.L.", "B10380194", esCritico: false);
        var empresa = new Empresa("Empresa Ruido Reclamado S.L.", "B10380186");
        contexto.Clientes.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var trabajadorReclamado = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "11111111H");
        var trabajadorSinReclamar = Trabajador.DeEmpresa(empresa.Id, "Luis", "Pérez", "22222222J");
        var tipoDocumento = new TipoDocumento("EPI", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, esObligatorio: true);
        contexto.Trabajadores.AddRange(trabajadorReclamado, trabajadorSinReclamar);
        contexto.TiposDocumento.Add(tipoDocumento);
        await contexto.SaveChangesAsync();

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var documentoReclamado = Documento.DeTrabajador(trabajadorReclamado.Id, tipoDocumento.Id, hoy, hoy.AddMonths(6));
        contexto.Documentos.Add(documentoReclamado);
        await contexto.SaveChangesAsync();

        var reclamacion = new ReclamacionDocumental(cliente.Id, Guid.NewGuid(), "cliente@ejemplo.com", DateTime.UtcNow, [documentoReclamado.Id]);
        contexto.ReclamacionesDocumentales.Add(reclamacion);
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _trabajadorReclamadoId = trabajadorReclamado.Id;
        _trabajadorSinReclamarId = trabajadorSinReclamar.Id;
        _tipoDocumentoId = tipoDocumento.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private async Task<SugerenciaGestionCorreo> SembrarSugerenciaConDosItemsAsync()
    {
        await using var contexto = CrearContexto();

        var conversacion = new Conversacion("Notificación en bloque", _clienteId);
        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "notificaciones@plataforma.com", "Aviso");
        await contexto.SaveChangesAsync();

        var sugerencia = new SugerenciaGestionCorreo(mensaje.Id, "Dos pendientes", 85);
        sugerencia.AgregarDetalle(_trabajadorReclamadoId, _tipoDocumentoId, 90, 90);
        sugerencia.AgregarDetalle(_trabajadorSinReclamarId, _tipoDocumentoId, 90, 90);
        contexto.SugerenciasGestionCorreo.Add(sugerencia);
        await contexto.SaveChangesAsync();

        return sugerencia;
    }

    [Fact]
    public async Task Marca_como_repeticion_solo_el_item_con_reclamacion_formal_previa()
    {
        var sugerencia = await SembrarSugerenciaConDosItemsAsync();
        var detalleReclamado = sugerencia.Detalles.Single(d => d.TrabajadorId == _trabajadorReclamadoId);
        var detalleSinReclamar = sugerencia.Detalles.Single(d => d.TrabajadorId == _trabajadorSinReclamarId);

        await using var contexto = CrearContexto();
        var repositorio = new ClasificacionRuidoDetalleGestionRepository(contexto);
        var servicio = new ClasificacionRuidoMensajeService(contexto, contexto, repositorio);

        await servicio.ProcesarAsync(sugerencia, _clienteId, esNotificacionAutomatica: true);
        await contexto.SaveChangesAsync();

        var clasificaciones = await contexto.ClasificacionesRuidoDetalleGestion.ToListAsync();
        clasificaciones.Should().ContainSingle(c => c.DetalleSugerenciaGestionCorreoId == detalleReclamado.Id);
        clasificaciones.Should().NotContain(c => c.DetalleSugerenciaGestionCorreoId == detalleSinReclamar.Id);
    }

    [Fact]
    public async Task No_clasifica_nada_si_el_mensaje_no_es_notificacion_automatica()
    {
        var sugerencia = await SembrarSugerenciaConDosItemsAsync();

        await using var contexto = CrearContexto();
        var repositorio = new ClasificacionRuidoDetalleGestionRepository(contexto);
        var servicio = new ClasificacionRuidoMensajeService(contexto, contexto, repositorio);

        await servicio.ProcesarAsync(sugerencia, _clienteId, esNotificacionAutomatica: false);
        await contexto.SaveChangesAsync();

        (await contexto.ClasificacionesRuidoDetalleGestion.CountAsync()).Should().Be(0);
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
