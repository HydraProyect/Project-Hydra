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
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto(_tenantA);
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
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
    public Task Aislamiento_Centro()
    {
        Guid clienteId = default, empresaId = default;
        return VerificarAislamientoAsync(
            () => new Centro(clienteId, empresaId, "Planta Sevilla"),
            async contexto =>
            {
                clienteId = await SembrarClienteAsync(contexto);
                empresaId = await SembrarEmpresaAsync(contexto);
            });
    }

    [Fact]
    public Task Aislamiento_Documento()
    {
        Guid trabajadorId = default, tipoDocumentoId = default;
        return VerificarAislamientoAsync(
            () => Documento.DeTrabajador(trabajadorId, tipoDocumentoId, new DateOnly(2026, 1, 1), null),
            async contexto =>
            {
                trabajadorId = await SembrarTrabajadorAsync(contexto);
                tipoDocumentoId = await SembrarTipoDocumentoAsync(contexto);
            });
    }

    [Fact]
    public Task Aislamiento_Empresa() => VerificarAislamientoAsync(
        () => new Empresa("Ibertec S.A."));

    [Fact]
    public Task Aislamiento_RequisitoDocumental()
    {
        Guid centroId = default;
        return VerificarAislamientoAsync(
            () => new RequisitoDocumental(centroId, "AEAT nominativo", null, bloqueaAcceso: false),
            async contexto => centroId = await SembrarCentroAsync(contexto));
    }

    [Fact]
    public Task Aislamiento_Subcontrata() => VerificarAislamientoAsync(
        () => new Subcontrata("Subcontrata de prueba"));

    [Fact]
    public Task Aislamiento_Trabajador()
    {
        Guid empresaId = default;
        return VerificarAislamientoAsync(
            () => Trabajador.DeEmpresa(empresaId, "Juan", "Pérez", "12345678Z"),
            async contexto => empresaId = await SembrarEmpresaAsync(contexto));
    }

    [Fact]
    public Task Aislamiento_Vehiculo()
    {
        Guid empresaId = default;
        return VerificarAislamientoAsync(
            () => Vehiculo.DeEmpresa(empresaId, "Furgoneta 1", "Modelo X", "1234ABC"),
            async contexto => empresaId = await SembrarEmpresaAsync(contexto));
    }

    [Fact]
    public Task Aislamiento_Visita()
    {
        Guid centroId = default;
        return VerificarAislamientoAsync(
            () => new Visita(centroId, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), null),
            async contexto => centroId = await SembrarCentroAsync(contexto));
    }

    [Fact]
    public Task Aislamiento_Proyecto()
    {
        Guid clienteId = default, centroId = default;
        return VerificarAislamientoAsync(
            () => Proyecto.Crear(clienteId, centroId, "Parada técnica 2026", new DateOnly(2026, 1, 1), null, null),
            async contexto =>
            {
                clienteId = await SembrarClienteAsync(contexto);
                centroId = await SembrarCentroAsync(contexto);
            });
    }

    [Fact]
    public Task Aislamiento_Evaluacion()
    {
        Guid centroId = default;
        return VerificarAislamientoAsync(
            () => new Evaluacion(centroId, null, new DateOnly(2026, 1, 1), 80, null),
            async contexto => centroId = await SembrarCentroAsync(contexto));
    }

    [Fact]
    public Task Aislamiento_Incidencia()
    {
        Guid centroId = default;
        return VerificarAislamientoAsync(
            () => new Incidencia(centroId, null, TipoIncidencia.Accidente, GravedadIncidencia.Leve,
                new DateOnly(2026, 1, 1), "Descripción de prueba"),
            async contexto => centroId = await SembrarCentroAsync(contexto));
    }

    // Hallazgo A-1 de INFORME-AUDITORIA-TECNICA.md: TarifaCliente heredaba de
    // EntidadBase pero se quedó sin HasQueryFilter, así que
    // ObtenerTarifasClienteQuery devolvía tarifas de cualquier tenant (y
    // también las borradas lógicamente). Divulgación cruzada de precios
    // comerciales entre consultoras.
    [Fact]
    public Task Aislamiento_TarifaCliente()
    {
        Guid clienteId = default;
        return VerificarAislamientoAsync(
            () => TarifaCliente.Crear(clienteId, ConceptoFacturable.TrabajadorActivo, 12.50m, "EUR"),
            async contexto => clienteId = await SembrarClienteAsync(contexto));
    }

    [Fact]
    public Task Aislamiento_ConversacionCorreo() => VerificarAislamientoAsync(
        () => new ConversacionCorreo("Consulta sobre documentación"));

    [Fact]
    public Task Aislamiento_MacroRespuesta() => VerificarAislamientoAsync(
        () => new MacroRespuesta("Falta el apto médico", "<p>Nos falta el apto médico.</p>"));

    // --- tablas de unión/satélite (EntidadConTenant directa, sin soft delete) ---

    [Fact]
    public Task Aislamiento_Alerta()
    {
        Guid documentoId = default;
        return VerificarAislamientoAsync(
            () => new Alerta(documentoId, NivelAlerta.Urgente),
            async contexto => documentoId = await SembrarDocumentoAsync(contexto));
    }

    [Fact]
    public Task Aislamiento_Asignacion()
    {
        Guid trabajadorId = default, centroId = default;
        return VerificarAislamientoAsync(
            () => new Asignacion(trabajadorId, centroId, new DateOnly(2026, 1, 1)),
            async contexto =>
            {
                trabajadorId = await SembrarTrabajadorAsync(contexto);
                centroId = await SembrarCentroAsync(contexto);
            });
    }

    [Fact]
    public Task Aislamiento_RegistroAuditoria() => VerificarAislamientoAsync(
        () => new RegistroAuditoria("Cliente", Guid.NewGuid(), "Creado", null, "{}", null));

    [Fact]
    public Task Aislamiento_CanalGestionDocumental()
    {
        Guid centroId = default;
        return VerificarAislamientoAsync(
            () => CanalGestionDocumental.DePlataforma(centroId, "CTAIMA CAE", null, null, null),
            async contexto => centroId = await SembrarCentroAsync(contexto));
    }

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
    public Task Aislamiento_TipoDocumentoCentro()
    {
        Guid tipoDocumentoId = default, centroId = default;
        return VerificarAislamientoAsync(
            () => new TipoDocumentoCentro(tipoDocumentoId, centroId),
            async contexto =>
            {
                tipoDocumentoId = await SembrarTipoDocumentoAsync(contexto);
                centroId = await SembrarCentroAsync(contexto);
            });
    }

    [Fact]
    public Task Aislamiento_CredencialAccesoEmpresa() => VerificarAislamientoAsync(
        () => new CredencialAccesoEmpresa(Guid.NewGuid(), null, null, null, null));

    [Fact]
    public Task Aislamiento_EmpresaCliente()
    {
        Guid empresaId = default, clienteId = default;
        return VerificarAislamientoAsync(
            () => new EmpresaCliente(empresaId, clienteId),
            async contexto =>
            {
                empresaId = await SembrarEmpresaAsync(contexto);
                clienteId = await SembrarClienteAsync(contexto);
            });
    }

    [Fact]
    public Task Aislamiento_NotificacionUsuario() => VerificarAislamientoAsync(
        () => new NotificacionUsuario(Guid.NewGuid(), "Título", "Mensaje"));

    [Fact]
    public Task Aislamiento_CredencialAccesoSubcontrata() => VerificarAislamientoAsync(
        () => new CredencialAccesoSubcontrata(Guid.NewGuid(), null, null, null, null));

    [Fact]
    public Task Aislamiento_SubcontrataCliente()
    {
        Guid subcontrataId = default, clienteId = default;
        return VerificarAislamientoAsync(
            () => new SubcontrataCliente(subcontrataId, clienteId),
            async contexto =>
            {
                subcontrataId = await SembrarSubcontrataAsync(contexto);
                clienteId = await SembrarClienteAsync(contexto);
            });
    }

    [Fact]
    public Task Aislamiento_SubcontrataEmpresa()
    {
        Guid subcontrataId = default, empresaId = default;
        return VerificarAislamientoAsync(
            () => new SubcontrataEmpresa(subcontrataId, empresaId),
            async contexto =>
            {
                subcontrataId = await SembrarSubcontrataAsync(contexto);
                empresaId = await SembrarEmpresaAsync(contexto);
            });
    }

    [Fact]
    public Task Aislamiento_DeteccionTrabajador() => VerificarAislamientoAsync(
        () => DeteccionTrabajador.Nuevo(Guid.NewGuid(), Guid.NewGuid(), "Juan", "Pérez", "12345678Z"));

    [Fact]
    public Task Aislamiento_VisitaTrabajador()
    {
        Guid visitaId = default, trabajadorId = default;
        return VerificarAislamientoAsync(
            () => new VisitaTrabajador(visitaId, trabajadorId),
            async contexto =>
            {
                visitaId = await SembrarVisitaAsync(contexto);
                trabajadorId = await SembrarTrabajadorAsync(contexto);
            });
    }

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
    public Task Aislamiento_ProyectoTecnico()
    {
        Guid proyectoId = default, trabajadorId = default;
        return VerificarAislamientoAsync(
            () => new ProyectoTecnico(proyectoId, trabajadorId, new DateOnly(2026, 1, 1)),
            async contexto =>
            {
                proyectoId = await SembrarProyectoAsync(contexto);
                trabajadorId = await SembrarTrabajadorAsync(contexto);
            });
    }

    [Fact]
    public Task Aislamiento_TrabajoAnalisisDocumento() => VerificarAislamientoAsync(
        () => new TrabajoAnalisisDocumento(Guid.NewGuid(), Guid.NewGuid(), TipoAnalisisDocumento.VerificacionIa));

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

    // --- Siembra de dependencias para las entidades con FK real (P0-1 de
    // docs/business/MATURITY_REVIEW.md) — antes se podía insertar cualquier
    // Guid inventado como propietario; ahora la base de datos lo rechaza,
    // así que estos tests necesitan un padre real en el contexto del tenant A.
    // Varios tests siembran el mismo tipo de padre más de una vez en el mismo
    // tenant (p. ej. Trabajador y Centro siembran cada uno su propia Empresa),
    // así que cada llamada genera un identificador único y válido — reusar uno
    // fijo chocaría con los índices únicos (TenantId, Cif)/(TenantId, RazonSocial)/
    // (TenantId, Dni)/(TenantId, Nombre).

    private static int _contadorSiembra;

    private static async Task<Guid> SembrarClienteAsync(CaeManagerDbContext contexto)
    {
        var cliente = new Cliente($"Cliente de prueba {Guid.NewGuid():N}", GenerarCifValido(), esCritico: false);
        contexto.Clientes.Add(cliente);
        await contexto.SaveChangesAsync();
        return cliente.Id;
    }

    private static async Task<Guid> SembrarEmpresaAsync(CaeManagerDbContext contexto)
    {
        var empresa = new Empresa($"Empresa de prueba {Guid.NewGuid():N}");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();
        return empresa.Id;
    }

    private static async Task<Guid> SembrarSubcontrataAsync(CaeManagerDbContext contexto)
    {
        var subcontrata = new Subcontrata($"Subcontrata de prueba {Guid.NewGuid():N}");
        contexto.Subcontratas.Add(subcontrata);
        await contexto.SaveChangesAsync();
        return subcontrata.Id;
    }

    private static async Task<Guid> SembrarCentroAsync(CaeManagerDbContext contexto)
    {
        var clienteId = await SembrarClienteAsync(contexto);
        var empresaId = await SembrarEmpresaAsync(contexto);
        var centro = new Centro(clienteId, empresaId, "Centro de prueba");
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();
        return centro.Id;
    }

    private static async Task<Guid> SembrarTrabajadorAsync(CaeManagerDbContext contexto)
    {
        var empresaId = await SembrarEmpresaAsync(contexto);
        var trabajador = Trabajador.DeEmpresa(empresaId, "Juan", "Pérez", GenerarDniValido());
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();
        return trabajador.Id;
    }

    private static async Task<Guid> SembrarTipoDocumentoAsync(CaeManagerDbContext contexto)
    {
        var tipoDocumento = new TipoDocumento($"Tipo de prueba {Guid.NewGuid():N}", 12, true, 1, AmbitoAplicacion.Trabajador);
        contexto.TiposDocumento.Add(tipoDocumento);
        await contexto.SaveChangesAsync();
        return tipoDocumento.Id;
    }

    /// <summary>CIF con dígito de control real, calculado con el mismo algoritmo que ValidadorIdentificacion (organización "B", control numérico).</summary>
    private static string GenerarCifValido()
    {
        var digitos = System.Threading.Interlocked.Increment(ref _contadorSiembra).ToString().PadLeft(7, '0');
        var sumaPares = 0;
        var sumaImpares = 0;
        for (var i = 0; i < digitos.Length; i++)
        {
            var num = digitos[i] - '0';
            if (i % 2 == 1)
            {
                sumaPares += num;
            }
            else
            {
                var multiplicado = num * 2;
                sumaImpares += multiplicado > 9 ? multiplicado - 9 : multiplicado;
            }
        }

        var residuo = (sumaPares + sumaImpares) % 10;
        var digitoControl = residuo == 0 ? 0 : 10 - residuo;
        return $"B{digitos}{digitoControl}";
    }

    /// <summary>DNI con letra de control real (algoritmo estándar módulo 23).</summary>
    private static string GenerarDniValido()
    {
        const string letras = "TRWAGMYFPDXBNJZSQVHLCKE";
        var numero = System.Threading.Interlocked.Increment(ref _contadorSiembra);
        return $"{numero:D8}{letras[numero % 23]}";
    }

    private static async Task<Guid> SembrarDocumentoAsync(CaeManagerDbContext contexto)
    {
        var trabajadorId = await SembrarTrabajadorAsync(contexto);
        var tipoDocumentoId = await SembrarTipoDocumentoAsync(contexto);
        var documento = Documento.DeTrabajador(trabajadorId, tipoDocumentoId, new DateOnly(2026, 1, 1), null);
        contexto.Documentos.Add(documento);
        await contexto.SaveChangesAsync();
        return documento.Id;
    }

    private static async Task<Guid> SembrarProyectoAsync(CaeManagerDbContext contexto)
    {
        var clienteId = await SembrarClienteAsync(contexto);
        var centroId = await SembrarCentroAsync(contexto);
        var proyecto = Proyecto.Crear(clienteId, centroId, $"Proyecto {Guid.NewGuid():N}", new DateOnly(2026, 1, 1), null, null);
        contexto.Proyectos.Add(proyecto);
        await contexto.SaveChangesAsync();
        return proyecto.Id;
    }

    private static async Task<Guid> SembrarVisitaAsync(CaeManagerDbContext contexto)
    {
        var centroId = await SembrarCentroAsync(contexto);
        var visita = new Visita(centroId, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), null);
        contexto.Visitas.Add(visita);
        await contexto.SaveChangesAsync();
        return visita.Id;
    }
}
