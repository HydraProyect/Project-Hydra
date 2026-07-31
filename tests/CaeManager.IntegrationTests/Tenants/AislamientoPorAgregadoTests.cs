using CaeManager.Domain.Alertas;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Evaluaciones;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Incidencias;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.RequisitosDocumentales;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Cierre de la Etapa 5 de PLAN-MIGRACION-MULTITENANT.md: test de
/// aislamiento por cada uno de los <b>38</b> tipos que heredan de
/// <c>EntidadConTenant</c>/<c>EntidadBase</c> — uno por cada línea de
/// <c>HasQueryFilter</c> de <c>CaeManagerDbContext</c>, sin excepciones
/// (regla de docs/MULTITENANCY.md § 9 — "los tests de aislamiento se
/// escriben por agregado... toda entidad nueva añade el suyo").
///
/// El fichero nació cubriendo 25 y se quedó atrás según crecía el modelo:
/// llegó a faltar el test de 9 entidades, dos de las cuales (TarifaCliente y
/// AprobacionDocumento) resultaron no tener siquiera el filtro — hallazgos
/// A-1 y M-1 de INFORME-AUDITORIA-TECNICA.md. Las 4 de Comunicaciones
/// (Fase 59) sí traían filtro pero llegaron sin test. Si añades una entidad
/// con TenantId, añade aquí su test.
///
/// <see cref="AislamientoMultiTenantTests"/> ya
/// prueba con más profundidad de escenario (fallo cerrado, rechazo de
/// modificación cruzada, índice único) sobre dos entidades representativas
/// (Cliente/Alerta); este archivo cierra la cobertura completa de las 38,
/// con la misma verificación de visibilidad en cada una.
/// </summary>
public class AislamientoPorAgregadoTests : IAsyncLifetime
{
    private readonly string _rutaBaseDatos = Path.Combine(Path.GetTempPath(), $"caemanager-aislamiento-agregado-{Guid.NewGuid()}.db");
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto(_tenantA);
        await dbContext.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        // Microsoft.Data.Sqlite devuelve la conexión a su pool al disponer el
        // contexto, y ese handle sigue abierto: en Windows impide borrar el
        // archivo y hacía fallar el teardown (no el cuerpo) del test.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_rutaBaseDatos)) File.Delete(_rutaBaseDatos);
        return Task.CompletedTask;
    }

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseSqlite($"Data Source={_rutaBaseDatos}")
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <param name="sembrarDependencias">
    /// Para las entidades con clave foránea real (las de Comunicaciones
    /// apuntan a ConversacionCorreo), que no se pueden insertar con un Guid
    /// inventado: siembra el padre en el contexto del tenant A antes de
    /// construir la entidad, de modo que <paramref name="crear"/> pueda
    /// capturar su Id.
    /// </param>
    private async Task VerificarAislamientoAsync<TEntidad>(
        Func<TEntidad> crear, Func<CaeManagerDbContext, Task>? sembrarDependencias = null) where TEntidad : EntidadConTenant
    {
        Guid id;
        await using (var contextoA = CrearContexto(_tenantA))
        {
            if (sembrarDependencias is not null)
                await sembrarDependencias(contextoA);

            var entidad = crear();
            contextoA.Set<TEntidad>().Add(entidad);
            await contextoA.SaveChangesAsync();
            id = entidad.Id;
        }

        await using var contextoB = CrearContexto(_tenantB);
        var visibleParaB = await contextoB.Set<TEntidad>().FirstOrDefaultAsync(e => e.Id == id);
        visibleParaB.Should().BeNull($"una fila de {typeof(TEntidad).Name} creada por otro tenant nunca debe ser visible");

        await using var contextoAOtraVez = CrearContexto(_tenantA);
        var visibleParaA = await contextoAOtraVez.Set<TEntidad>().FirstOrDefaultAsync(e => e.Id == id);
        visibleParaA.Should().NotBeNull($"el propio tenant que creó la fila de {typeof(TEntidad).Name} debe poder verla");
    }

    // --- agregados con soft delete (EntidadBase) ---

    [Fact]
    public Task Aislamiento_Cliente() => VerificarAislamientoAsync(
        () => new Cliente("RENDELSUR", "B12345674", esCritico: false));

    [Fact]
    public Task Aislamiento_Centro() => VerificarAislamientoAsync(
        () => new Centro(Guid.NewGuid(), Guid.NewGuid(), "Planta Sevilla"));

    [Fact]
    public Task Aislamiento_Documento() => VerificarAislamientoAsync(
        () => Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), null));

    [Fact]
    public Task Aislamiento_Empresa() => VerificarAislamientoAsync(
        () => new Empresa("Ibertec S.A."));

    [Fact]
    public Task Aislamiento_RequisitoDocumental() => VerificarAislamientoAsync(
        () => new RequisitoDocumental(Guid.NewGuid(), "AEAT nominativo", null, bloqueaAcceso: false));

    [Fact]
    public Task Aislamiento_Subcontrata() => VerificarAislamientoAsync(
        () => new Subcontrata("Subcontrata de prueba"));

    [Fact]
    public Task Aislamiento_Trabajador() => VerificarAislamientoAsync(
        () => Trabajador.DeEmpresa(Guid.NewGuid(), "Juan", "Pérez", "12345678Z"));

    [Fact]
    public Task Aislamiento_Vehiculo() => VerificarAislamientoAsync(
        () => Vehiculo.DeEmpresa(Guid.NewGuid(), "Furgoneta 1", "Modelo X", "1234ABC"));

    [Fact]
    public Task Aislamiento_Visita() => VerificarAislamientoAsync(
        () => new Visita(Guid.NewGuid(), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), null));

    [Fact]
    public Task Aislamiento_Proyecto() => VerificarAislamientoAsync(
        () => Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Parada técnica 2026", new DateOnly(2026, 1, 1), null, null));

    [Fact]
    public Task Aislamiento_Evaluacion() => VerificarAislamientoAsync(
        () => new Evaluacion(Guid.NewGuid(), null, new DateOnly(2026, 1, 1), 80, null));

    [Fact]
    public Task Aislamiento_Incidencia() => VerificarAislamientoAsync(
        () => new Incidencia(Guid.NewGuid(), null, TipoIncidencia.Accidente, GravedadIncidencia.Leve,
            new DateOnly(2026, 1, 1), "Descripción de prueba"));

    // Hallazgo A-1 de INFORME-AUDITORIA-TECNICA.md: TarifaCliente heredaba de
    // EntidadBase pero se quedó sin HasQueryFilter, así que
    // ObtenerTarifasClienteQuery devolvía tarifas de cualquier tenant (y
    // también las borradas lógicamente). Divulgación cruzada de precios
    // comerciales entre consultoras.
    [Fact]
    public Task Aislamiento_TarifaCliente() => VerificarAislamientoAsync(
        () => TarifaCliente.Crear(Guid.NewGuid(), ConceptoFacturable.TrabajadorActivo, 12.50m, "EUR"));

    [Fact]
    public Task Aislamiento_ConversacionCorreo() => VerificarAislamientoAsync(
        () => new ConversacionCorreo("Consulta sobre documentación"));

    [Fact]
    public Task Aislamiento_MacroRespuesta() => VerificarAislamientoAsync(
        () => new MacroRespuesta("Falta el apto médico", "<p>Nos falta el apto médico.</p>"));

    // --- tablas de unión/satélite (EntidadConTenant directa, sin soft delete) ---

    [Fact]
    public Task Aislamiento_Alerta() => VerificarAislamientoAsync(
        () => new Alerta(Guid.NewGuid(), NivelAlerta.Urgente));

    [Fact]
    public Task Aislamiento_Asignacion() => VerificarAislamientoAsync(
        () => new Asignacion(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1)));

    [Fact]
    public Task Aislamiento_RegistroAuditoria() => VerificarAislamientoAsync(
        () => new RegistroAuditoria("Cliente", Guid.NewGuid(), "Creado", null, "{}", null));

    [Fact]
    public Task Aislamiento_CanalGestionDocumental() => VerificarAislamientoAsync(
        () => CanalGestionDocumental.DePlataforma(Guid.NewGuid(), "CTAIMA CAE", null, null, null));

    [Fact]
    public Task Aislamiento_ParametroSistema() => VerificarAislamientoAsync(
        () => new ParametroSistema(30, 15));

    [Fact]
    public Task Aislamiento_ConfiguracionIaDocumentoCliente() => VerificarAislamientoAsync(
        () => new ConfiguracionIaDocumentoCliente(Guid.NewGuid(), Guid.NewGuid(), activa: true));

    [Fact]
    public Task Aislamiento_TipoDocumento() => VerificarAislamientoAsync(
        () => new TipoDocumento("Tipo de prueba", 12, true, 1, AmbitoAplicacion.Trabajador));

    [Fact]
    public Task Aislamiento_TipoDocumentoCentro() => VerificarAislamientoAsync(
        () => new TipoDocumentoCentro(Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public Task Aislamiento_CredencialAccesoEmpresa() => VerificarAislamientoAsync(
        () => new CredencialAccesoEmpresa(Guid.NewGuid(), null, null, null, null));

    [Fact]
    public Task Aislamiento_EmpresaCliente() => VerificarAislamientoAsync(
        () => new EmpresaCliente(Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public Task Aislamiento_NotificacionUsuario() => VerificarAislamientoAsync(
        () => new NotificacionUsuario(Guid.NewGuid(), "Título", "Mensaje"));

    [Fact]
    public Task Aislamiento_CredencialAccesoSubcontrata() => VerificarAislamientoAsync(
        () => new CredencialAccesoSubcontrata(Guid.NewGuid(), null, null, null, null));

    [Fact]
    public Task Aislamiento_SubcontrataCliente() => VerificarAislamientoAsync(
        () => new SubcontrataCliente(Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public Task Aislamiento_SubcontrataEmpresa() => VerificarAislamientoAsync(
        () => new SubcontrataEmpresa(Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public Task Aislamiento_DeteccionTrabajador() => VerificarAislamientoAsync(
        () => DeteccionTrabajador.Nuevo(Guid.NewGuid(), Guid.NewGuid(), "Juan", "Pérez", "12345678Z"));

    [Fact]
    public Task Aislamiento_VisitaTrabajador() => VerificarAislamientoAsync(
        () => new VisitaTrabajador(Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public Task Aislamiento_ExtraccionIaCache() => VerificarAislamientoAsync(
        () => ExtraccionIaCache.Crear(new string('a', 64), "{}"));

    [Fact]
    public Task Aislamiento_AuditoriaExtraccionIa() => VerificarAislamientoAsync(
        () => AuditoriaExtraccionIa.Crear(new string('b', 64), "Apto médico", "anthropic", 1200, null, null, 1, 95, null));

    [Fact]
    public Task Aislamiento_RevisionIaDocumento() => VerificarAislamientoAsync(
        () => RevisionIaDocumento.Crear(Guid.NewGuid(), 40, "Apto médico", null, null, null, "Confianza insuficiente"));

    [Fact]
    public Task Aislamiento_ProyectoTecnico() => VerificarAislamientoAsync(
        () => new ProyectoTecnico(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1)));

    // Hallazgo M-1: AprobacionDocumento tampoco tenía filtro. No fugaba
    // todavía porque su único lector hace join contra Documentos (que sí está
    // filtrado), pero la invariante estaba rota y cualquier consulta futura
    // que leyera la tabla directamente habría cruzado tenants en silencio.
    [Fact]
    public Task Aislamiento_AprobacionDocumento() => VerificarAislamientoAsync(
        () => AprobacionDocumento.CrearAutomatica(Guid.NewGuid(), 95));

    [Fact]
    public async Task Aislamiento_MensajeCorreo()
    {
        var conversacionId = Guid.Empty;

        await VerificarAislamientoAsync(
            () => new MensajeCorreo(conversacionId, DireccionMensaje.Entrante, "remitente@ejemplo.com",
                "<p>Cuerpo del mensaje.</p>", new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)),
            async contexto => conversacionId = await SembrarConversacionAsync(contexto));
    }

    [Fact]
    public async Task Aislamiento_ParticipanteConversacion()
    {
        var conversacionId = Guid.Empty;

        await VerificarAislamientoAsync(
            () => new ParticipanteConversacion(conversacionId, "participante@ejemplo.com",
                RolParticipante.Para, TipoParticipanteOrigen.Trabajador),
            async contexto => conversacionId = await SembrarConversacionAsync(contexto));
    }

    /// <summary>
    /// El registro de actividad de soporte pertenece al tenant <b>visitado</b>,
    /// no al de Hydra: es el cliente quien debe poder consultar qué se hizo en
    /// sus datos. Por eso lleva filtro global como todo lo demás, y por eso un
    /// tenant no puede ver las visitas de soporte a otro.
    /// </summary>
    [Fact]
    public Task Aislamiento_RegistroActividadSoporte() => VerificarAislamientoAsync(
        () => new RegistroActividadSoporte(
            Guid.NewGuid(), Guid.NewGuid(), TipoActividadSoporte.Navegacion, "/documentos"));

    private static async Task<Guid> SembrarConversacionAsync(CaeManagerDbContext contexto)
    {
        var conversacion = new ConversacionCorreo("Conversación de prueba");
        contexto.ConversacionesCorreo.Add(conversacion);
        await contexto.SaveChangesAsync();
        return conversacion.Id;
    }
}
