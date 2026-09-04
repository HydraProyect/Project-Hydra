using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Siembra el ciclo documental avanzado sobre los Documentos que
/// <see cref="DatosPruebaSeeder"/> ya creó (PLAN-DATOS-PRUEBA.md, Tanda 2):
/// revisiones IA pendientes y resueltas, aprobaciones automáticas y
/// manuales, trabajos de análisis en sus estados, auditoría de extracción
/// IA, validación oficial (verificación + firmas digitales) y detecciones
/// de trabajadores — todo lo que antes solo existía como flag de catálogo y
/// dejaba `/documentos/revision-ia`, `/auditoria-ia` y la detección de
/// trabajadores sin ningún dato con el que probarse.
///
/// Igual que <see cref="ComunicacionesDatosPruebaSeeder"/>: corre solo para
/// el tenant de demo principal (el único con usuarios de prueba), dentro del
/// ambiente de tenant que establezca el invocador, con guard idempotente
/// propio y apagado por defecto (<c>DatosPrueba:Activo</c>). Datos 100 %
/// sintéticos con la misma regla de ficción que el resto de la siembra.
/// </summary>
public static class CicloDocumentalDatosPruebaSeeder
{
    public static async Task SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("DatosPrueba:Activo"))
            return;

        if (await dbContext.RevisionesIaDocumento.AnyAsync(cancellationToken))
            return;

        var documentosTrabajador = await dbContext.Documentos
            .Where(d => d.TrabajadorId != null)
            .OrderBy(d => d.CreadoEnUtc).ThenBy(d => d.Id)
            .Take(12)
            .ToListAsync(cancellationToken);

        if (documentosTrabajador.Count < 12)
        {
            logger.LogInformation("No hay documentos de trabajador suficientes — se omite la siembra del ciclo documental.");
            return;
        }

        SembrarRevisionesIa(dbContext, documentosTrabajador);
        await SembrarAprobacionesAsync(dbContext, userManager, documentosTrabajador);
        SembrarTrabajosAnalisis(dbContext, documentosTrabajador);
        SembrarAuditoriaExtraccion(dbContext);
        await SembrarValidacionOficialAsync(dbContext, cancellationToken);
        await SembrarDeteccionesTrabajadorAsync(dbContext, cancellationToken);
        await SembrarAcreditacionesAsync(dbContext, cancellationToken);
        await SembrarPlantillasAsync(dbContext, userManager, documentosTrabajador, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Ciclo documental de prueba sembrado: revisiones IA, aprobaciones, trabajos de análisis, auditoría, validación oficial, detecciones y plantillas.");
    }

    /// <summary>
    /// REC-061: hasta este cambio, todo el dominio de Plantillas (ADR-010)
    /// tenía cero filas en la siembra de demo — <c>/plantillas</c> y
    /// "Documentos generados" arrancaban siempre vacíos, así que ninguno de
    /// sus estados (catálogo con/sin confirmar, documento generado con y sin
    /// avisos — DEC-5, 2026-09-02) se podía revisar sin generarlos a mano.
    ///
    /// Siembra mínima y determinista: una plantilla confirmada (con la que
    /// se genera) y una en Borrador (para que el catálogo no muestre solo el
    /// estado feliz), más dos <see cref="DocumentoGenerado"/> reales sobre la
    /// confirmada — uno sin avisos y uno con.
    ///
    /// <para>
    /// El tipo de documento de la plantilla es <c>"Riesgo Eléctrico"</c> —
    /// NINGÚN otro punto de esta siembra crea documentos de ese tipo (los 7
    /// estándar y los 2 obligatorios sin vencimiento son otros, ver
    /// <see cref="DocumentacionEstandarTrabajador"/> en <c>DatosPruebaSeeder</c>).
    /// La primera versión de este cambio reutilizaba "Entrega de EPI"
    /// —uno de los 7 estándar— sobre dos Trabajador tomados de
    /// <paramref name="documentosTrabajador"/> por posición: como todos los
    /// <c>Documento</c> de esa tanda se guardan en un único <c>SaveChanges</c>,
    /// comparten (o casi) el mismo <c>CreadoEnUtc</c>, así que el
    /// <c>.ThenBy(Id)</c> que desempata ese <c>OrderBy</c> no ordena por
    /// Trabajador — dos posiciones cercanas podían caer en el MISMO
    /// Trabajador, que terminaba con tres "Entrega de EPI" en vez de una.
    /// Reproducido en CI (E2E, <c>DeepLinksTests.Deep_link_de_Centro_reconstruye_el_panel_en_frio</c>):
    /// no hacía falta entender el mecanismo exacto para eliminar el riesgo —
    /// un tipo que nadie más siembra no puede duplicar nada.
    /// </para>
    /// </summary>
    /// <summary>internal (no private) a propósito: PlantillasDatosPruebaSeederTests la ejercita sin depender de la cartera GestorCae completa.</summary>
    internal static async Task SembrarPlantillasAsync(
        CaeManagerDbContext dbContext, UserManager<ApplicationUser> userManager,
        List<Documento> documentosTrabajador, CancellationToken cancellationToken)
    {
        var tipoPlantilla = await dbContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Nombre == "Riesgo Eléctrico", cancellationToken);
        var gestor = (await userManager.GetUsersInRoleAsync(Roles.GestorCae))
            .OrderBy(u => u.Email).FirstOrDefault();

        // Dos Trabajador REALMENTE distintos — .Distinct() sobre los ids, no
        // por posición en la lista (ver el porqué en el comentario de arriba).
        var trabajadorIds = documentosTrabajador
            .Select(d => d.TrabajadorId!.Value)
            .Distinct()
            .Take(2)
            .ToList();
        if (tipoPlantilla is null || gestor is null || trabajadorIds.Count < 2)
            return;

        var ahoraUtc = DateTime.UtcNow;
        var hoy = DateOnly.FromDateTime(ahoraUtc);

        // Segunda plantilla, deliberadamente SIN confirmar: si solo hubiera
        // una y estuviera confirmada, el catálogo de /plantillas no
        // enseñaría el badge "Borrador"/"Pendiente de revisión" que
        // Plantillas.razor también sabe pintar.
        var plantillaBorrador = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Autorización de acceso de visita (demo)", AmbitoAplicacion.Trabajador,
            FormatoOrigenPlantilla.PdfVisual, tipoPlantilla.Id,
            descripcion: "PDF de acceso pendiente de configurar sus campos — subido, sin elementos ni confirmar todavía.");
        dbContext.PlantillasDocumento.Add(plantillaBorrador);
        dbContext.PlantillasDocumentoVersion.Add(new PlantillaDocumentoVersion(
            plantillaBorrador.Id, 1, "plantillas-demo/autorizacion-acceso-v1.pdf", new string('7', PlantillaDocumentoVersion.LongitudHashSha256)));

        var plantilla = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Formulario de riesgo eléctrico (demo)", AmbitoAplicacion.Trabajador,
            FormatoOrigenPlantilla.PdfVisual, tipoPlantilla.Id,
            descripcion: "Formulario que el centro exige firmar para el riesgo eléctrico — se rellena solo con datos del trabajador.");
        dbContext.PlantillasDocumento.Add(plantilla);

        var version = new PlantillaDocumentoVersion(
            plantilla.Id, 1, "plantillas-demo/formulario-riesgo-electrico-v1.pdf", new string('8', PlantillaDocumentoVersion.LongitudHashSha256));
        version.EstablecerElementos([
            new PlantillaElemento(version.Id, TipoElementoPlantilla.Texto, 1, 60, 700, 220, 20, "Nombre del trabajador",
                FuenteDatoPlantilla.TrabajadorNombreCompleto, obligatorio: true),
            new PlantillaElemento(version.Id, TipoElementoPlantilla.Texto, 1, 60, 670, 220, 20, "DNI",
                FuenteDatoPlantilla.TrabajadorDni, obligatorio: true),
            new PlantillaElemento(version.Id, TipoElementoPlantilla.Texto, 1, 60, 640, 220, 20, "Puesto",
                FuenteDatoPlantilla.TrabajadorPuesto, obligatorio: true),
            new PlantillaElemento(version.Id, TipoElementoPlantilla.Firma, 1, 60, 600, 150, 40, "Firma del trabajador",
                rolFirmante: RolFirmantePlantilla.Trabajador)
        ]);
        version.Confirmar(gestor.Id, ahoraUtc);
        dbContext.PlantillasDocumentoVersion.Add(version);

        // SaveChanges intermedio, a propósito: PlantillaDocumento.VersionActualId
        // es una FK real hacia PlantillaDocumentoVersion, y esta versión lleva a
        // su vez una FK hacia la plantilla (PlantillaDocumentoId) — insertar las
        // dos filas NUEVAS en el mismo SaveChanges con VersionActualId ya
        // apuntado forma un ciclo que EF Core no puede ordenar (mismo motivo por
        // el que CrearPlantillaDocumentoCommandHandler nunca establece
        // VersionActualId en el alta: lo hace ConfirmarPlantillaDocumentoVersionCommandHandler,
        // aparte, sobre filas que ya existen). Aquí no hay un IUnitOfWork por
        // comando que separe las dos operaciones, así que el guardado intermedio
        // hace ese mismo papel.
        await dbContext.SaveChangesAsync(cancellationToken);
        plantilla.EstablecerVersionActual(version.Id);

        var trabajadorSinAvisosId = trabajadorIds[0];
        var trabajadorConAvisosId = trabajadorIds[1];
        var vencimiento = tipoPlantilla.AplicaVencimientoAutomatico ? hoy.AddMonths(tipoPlantilla.VigenciaMeses ?? 12) : (DateOnly?)null;

        // Sin avisos: los tres campos obligatorios se resolvieron con dato real.
        var documentoSinAvisos = Documento.DeTrabajador(
            trabajadorSinAvisosId, tipoPlantilla.Id, hoy, vencimiento, "plantillas-demo/generado-riesgo-electrico-sin-avisos.pdf");
        dbContext.Documentos.Add(documentoSinAvisos);
        dbContext.DocumentosGenerados.Add(new DocumentoGenerado(
            version.Id, documentoSinAvisos.Id, """{"Nombre del trabajador":"dato real","DNI":"dato real","Puesto":"dato real"}""",
            gestor.Id, ahoraUtc, trabajadorId: trabajadorSinAvisosId, conAvisos: false));

        // Con avisos (DEC-5, 2026-09-02): el Puesto del trabajador está en
        // blanco en Hydra, así que ese campo obligatorio queda sin resolver
        // — el documento se genera igual, con el badge de aviso.
        var documentoConAvisos = Documento.DeTrabajador(
            trabajadorConAvisosId, tipoPlantilla.Id, hoy, vencimiento, "plantillas-demo/generado-riesgo-electrico-con-avisos.pdf");
        dbContext.Documentos.Add(documentoConAvisos);
        dbContext.DocumentosGenerados.Add(new DocumentoGenerado(
            version.Id, documentoConAvisos.Id, """{"Nombre del trabajador":"dato real","DNI":"dato real","Puesto":""}""",
            gestor.Id, ahoraUtc, trabajadorId: trabajadorConAvisosId, conAvisos: true));
    }

    private static void SembrarRevisionesIa(CaeManagerDbContext dbContext, List<Documento> documentos)
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        // Tres pendientes con motivos distintos y dos ya resueltas.
        dbContext.RevisionesIaDocumento.Add(RevisionIaDocumento.Crear(
            documentos[0].Id, 58, "Certificado de aptitud médica", hoy.AddMonths(-11), hoy.AddDays(20), true,
            "Confianza baja (58%)."));
        dbContext.RevisionesIaDocumento.Add(RevisionIaDocumento.Crear(
            documentos[1].Id, 74, "Entrega de EPI", hoy.AddMonths(-2), null, false,
            "La fecha de emisión introducida no coincide con la detectada."));
        dbContext.RevisionesIaDocumento.Add(RevisionIaDocumento.Crear(
            documentos[2].Id, 66, "Contrato de Trabajo", hoy.AddMonths(-6), null, null,
            "El tipo detectado no coincide con el tipo del documento."));

        var resuelta1 = RevisionIaDocumento.Crear(
            documentos[3].Id, 61, "Formación Art. 19", hoy.AddMonths(-13), hoy.AddDays(-4), true,
            "Confianza baja (61%); la fecha de vencimiento detectada ya pasó.");
        resuelta1.Resolver();
        dbContext.RevisionesIaDocumento.Add(resuelta1);

        var resuelta2 = RevisionIaDocumento.Crear(
            documentos[4].Id, 70, "Reciclaje 4h", hoy.AddMonths(-3), hoy.AddMonths(9), true,
            "La fecha de vencimiento introducida no coincide con la detectada.");
        resuelta2.Resolver();
        dbContext.RevisionesIaDocumento.Add(resuelta2);
    }

    private static async Task SembrarAprobacionesAsync(
        CaeManagerDbContext dbContext, UserManager<ApplicationUser> userManager, List<Documento> documentos)
    {
        dbContext.AprobacionesDocumento.Add(AprobacionDocumento.CrearAutomatica(documentos[5].Id, 97));
        dbContext.AprobacionesDocumento.Add(AprobacionDocumento.CrearAutomatica(documentos[6].Id, 93));
        dbContext.AprobacionesDocumento.Add(AprobacionDocumento.CrearAutomatica(documentos[7].Id, 91));
        dbContext.AprobacionesDocumento.Add(AprobacionDocumento.CrearAutomatica(documentos[8].Id, 88));

        // Las manuales exigen un usuario que las resolvió — el primer
        // GestorCae de prueba, si existe (mismo criterio que la asignación
        // de conversaciones en ComunicacionesDatosPruebaSeeder).
        var gestor = (await userManager.GetUsersInRoleAsync(Roles.GestorCae))
            .OrderBy(u => u.Email).FirstOrDefault();
        if (gestor is null)
            return;

        dbContext.AprobacionesDocumento.Add(AprobacionDocumento.CrearManual(documentos[3].Id, 61, gestor.Id));
        dbContext.AprobacionesDocumento.Add(AprobacionDocumento.CrearManual(documentos[4].Id, 70, gestor.Id));
    }

    private static void SembrarTrabajosAnalisis(CaeManagerDbContext dbContext, List<Documento> documentos)
    {
        // Estados terminales y uno en proceso. Ningún trabajo "Pendiente" a
        // propósito: el ProcesadorAnalisisDocumentoHostedService lo
        // consumiría nada más arrancar llamando al proveedor de IA real
        // sobre un documento sin archivo.
        var completadoVerificacion = new TrabajoAnalisisDocumento(documentos[5].Id, null, TipoAnalisisDocumento.VerificacionIa);
        completadoVerificacion.MarcarEnProceso();
        completadoVerificacion.MarcarCompletado();
        dbContext.TrabajosAnalisisDocumento.Add(completadoVerificacion);

        var completadoFirma = new TrabajoAnalisisDocumento(documentos[6].Id, null, TipoAnalisisDocumento.VerificacionFirmaDigital);
        completadoFirma.MarcarEnProceso();
        completadoFirma.MarcarCompletado();
        dbContext.TrabajosAnalisisDocumento.Add(completadoFirma);

        var fallido = new TrabajoAnalisisDocumento(documentos[2].Id, null, TipoAnalisisDocumento.VerificacionIa);
        for (var intento = 0; intento < TrabajoAnalisisDocumento.MaximoIntentos; intento++)
        {
            fallido.MarcarEnProceso();
            fallido.RegistrarFallo("El proveedor de IA devolvió un error de cuota (simulado en datos de prueba).");
        }
        dbContext.TrabajosAnalisisDocumento.Add(fallido);

        var enProceso = new TrabajoAnalisisDocumento(documentos[0].Id, null, TipoAnalisisDocumento.VerificacionIa);
        enProceso.MarcarEnProceso();
        dbContext.TrabajosAnalisisDocumento.Add(enProceso);
    }

    private static void SembrarAuditoriaExtraccion(CaeManagerDbContext dbContext)
    {
        // Una fila por camino del router: proveedor real (con y sin
        // incidencias), OCR previo, resultado servido de caché (coste 0) y
        // "ninguno" (ningún proveedor disponible).
        dbContext.AuditoriasExtraccionIa.Add(AuditoriaExtraccionIa.Crear(
            new string('a', 64), "Certificado de aptitud médica", "anthropic", 2140, null, 0.012m, 2, 95, null));
        dbContext.AuditoriasExtraccionIa.Add(AuditoriaExtraccionIa.Crear(
            new string('b', 64), "ITA", "mistral-ocr", 5320, 0.004m, 0.021m, 6, 88, null));
        dbContext.AuditoriasExtraccionIa.Add(AuditoriaExtraccionIa.Crear(
            new string('c', 64), "Entrega de EPI", "gemini", 3810, null, 0.009m, 1, 62,
            "Confianza por debajo del umbral; se generó revisión IA."));
        dbContext.AuditoriasExtraccionIa.Add(AuditoriaExtraccionIa.Crear(
            new string('d', 64), "Certificado de aptitud médica", "cache", 12, null, 0m, 2, 95, null));
        dbContext.AuditoriasExtraccionIa.Add(AuditoriaExtraccionIa.Crear(
            new string('e', 64), "Contrato de Trabajo", "ninguno", 3, null, null, 0, 0,
            "Ningún proveedor de IA configurado para este análisis."));
    }

    private static async Task SembrarValidacionOficialAsync(CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var documentosOficiales = await (
            from documento in dbContext.Documentos
            join tipo in dbContext.TiposDocumento on documento.TipoDocumentoId equals tipo.Id
            where tipo.PerfilDocumentoOficial != PerfilDocumentoOficial.Ninguno && documento.EmpresaId != null
            orderby documento.CreadoEnUtc, documento.Id
            select new { Documento = documento, tipo.PerfilDocumentoOficial })
            .Take(3)
            .ToListAsync(cancellationToken);

        if (documentosOficiales.Count < 3)
            return;

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var ahoraUtc = DateTime.UtcNow;

        // 1. Auto-validado: firma válida de sello de órgano con LTV y cotejo coincidente.
        var autoValidado = documentosOficiales[0];
        dbContext.VerificacionesDocumentoOficial.Add(new VerificacionDocumentoOficial(
            autoValidado.Documento.Id, autoValidado.PerfilDocumentoOficial, NivelConfianzaDocumental.FirmaValida,
            "CEA-DEMO-0001", "B00000123", "Limpiezas Acme Demo S.L.", hoy.AddDays(-12), "2026-07",
            ResultadoCotejoDocumentoOficial.Coincide, DecisionValidacionOficial.AutoValidado, string.Empty));
        dbContext.FirmasDigitalesDocumento.Add(new FirmaDigitalDocumento(
            autoValidado.Documento.Id, 0, EstadoFirmaPdf.Valida, cadenaConfiable: true,
            ComprobacionRevocacion.EmbebidaLtv, "AC Sector Público de Springfield", "SN-DEMO-0001",
            esSelloDeOrgano: true, "Tesorería de Springfield", "Q0000001B",
            ahoraUtc.AddDays(-12), tieneSelloDeTiempo: true, cubreDocumentoCompleto: true, motivoInvalidez: null));

        // 2. Revisión requerida: firma válida pero sin revocación comprobable y CIF discrepante.
        var revisionRequerida = documentosOficiales[1];
        dbContext.VerificacionesDocumentoOficial.Add(new VerificacionDocumentoOficial(
            revisionRequerida.Documento.Id, revisionRequerida.PerfilDocumentoOficial, NivelConfianzaDocumental.FirmaValidaSinRevocacion,
            "CSV-DEMO-0002", "B00000456", "Mantenimiento MomCorp Demo S.A.", hoy.AddDays(-30), "2026-06",
            ResultadoCotejoDocumentoOficial.Discrepancia, DecisionValidacionOficial.RevisionRequerida,
            "El CIF detectado no coincide con el de la empresa del documento."));
        dbContext.FirmasDigitalesDocumento.Add(new FirmaDigitalDocumento(
            revisionRequerida.Documento.Id, 0, EstadoFirmaPdf.Valida, cadenaConfiable: true,
            ComprobacionRevocacion.NoDisponible, "AC Sector Público de Springfield", "SN-DEMO-0002",
            esSelloDeOrgano: true, "Agencia Tributaria de Springfield", "Q0000002C",
            ahoraUtc.AddDays(-30), tieneSelloDeTiempo: false, cubreDocumentoCompleto: true, motivoInvalidez: null));

        // 3. Sin firma válida: el circuito oficial no puede continuar.
        var sinFirma = documentosOficiales[2];
        dbContext.VerificacionesDocumentoOficial.Add(new VerificacionDocumentoOficial(
            sinFirma.Documento.Id, sinFirma.PerfilDocumentoOficial, NivelConfianzaDocumental.SoloLectura,
            null, null, null, null, null,
            ResultadoCotejoDocumentoOficial.NoAplicable, DecisionValidacionOficial.SinFirmaValida,
            "El PDF no contiene ninguna firma digital válida."));
        dbContext.FirmasDigitalesDocumento.Add(new FirmaDigitalDocumento(
            sinFirma.Documento.Id, 0, EstadoFirmaPdf.Invalida, cadenaConfiable: false,
            ComprobacionRevocacion.NoDisponible, "AC Desconocida", "SN-DEMO-0003",
            esSelloDeOrgano: false, firmanteNombre: null, firmanteNif: null,
            ahoraUtc.AddDays(-5), tieneSelloDeTiempo: false, cubreDocumentoCompleto: false,
            "La firma no cubre el documento completo y el certificado no es de un emisor confiable."));
    }

    private static async Task SembrarDeteccionesTrabajadorAsync(CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var documentosConDeteccion = await (
            from documento in dbContext.Documentos
            join tipo in dbContext.TiposDocumento on documento.TipoDocumentoId equals tipo.Id
            where tipo.DeteccionTrabajadoresActiva && documento.EmpresaId != null
            orderby documento.CreadoEnUtc, documento.Id
            select documento)
            .Take(2)
            .ToListAsync(cancellationToken);

        if (documentosConDeteccion.Count < 2)
            return;

        var documentoIta = documentosConDeteccion[0];
        var documentoRnt = documentosConDeteccion[1];

        // Nuevos: aparecen en el documento y no existen en el sistema —
        // caricaturas que DatosPruebaSeeder no usa en su plantilla.
        dbContext.DeteccionesTrabajador.Add(DeteccionTrabajador.Nuevo(
            documentoIta.Id, documentoIta.EmpresaId!.Value, "Otto", "Rocket", DatosPruebaSeeder.GenerarDniValido(48_100_001)));
        var nuevoResuelto = DeteccionTrabajador.Nuevo(
            documentoIta.Id, documentoIta.EmpresaId!.Value, "Reggie", "Rocket", DatosPruebaSeeder.GenerarDniValido(48_100_002));
        nuevoResuelto.Resolver("Creado");
        dbContext.DeteccionesTrabajador.Add(nuevoResuelto);

        // Ausentes: trabajadores activos de la empresa que no aparecen en el documento.
        var trabajadoresEmpresa = await dbContext.Trabajadores
            .Where(t => t.EmpresaId == documentoRnt.EmpresaId)
            .OrderBy(t => t.CreadoEnUtc).ThenBy(t => t.Id)
            .Take(2)
            .ToListAsync(cancellationToken);

        if (trabajadoresEmpresa.Count < 2)
            return;

        dbContext.DeteccionesTrabajador.Add(DeteccionTrabajador.Ausente(
            documentoRnt.Id, documentoRnt.EmpresaId!.Value, trabajadoresEmpresa[0].Id,
            trabajadoresEmpresa[0].Nombre, trabajadoresEmpresa[0].Apellidos, trabajadoresEmpresa[0].Dni!));
        var ausenteResuelto = DeteccionTrabajador.Ausente(
            documentoRnt.Id, documentoRnt.EmpresaId!.Value, trabajadoresEmpresa[1].Id,
            trabajadoresEmpresa[1].Nombre, trabajadoresEmpresa[1].Apellidos, trabajadoresEmpresa[1].Dni!);
        ausenteResuelto.Resolver("Mantenido");
        dbContext.DeteccionesTrabajador.Add(ausenteResuelto);
    }

    /// <summary>
    /// Los 5 estados de acreditación de un documento frente al canal de
    /// plataforma de un centro, con historial de rechazos append-only en la
    /// rechazada — los documentos son de la empresa que opera ese centro.
    /// </summary>
    internal static async Task SembrarAcreditacionesAsync(CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var canal = await dbContext.CanalesGestionDocumental
            .Where(c => c.Tipo == TipoCanalGestion.Plataforma)
            .OrderBy(c => c.CreadoEnUtc).ThenBy(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (canal is null)
            return;

        var centro = await dbContext.Centros.FirstAsync(c => c.Id == canal.CentroId, cancellationToken);
        var documentosEmpresa = await dbContext.Documentos
            .Where(d => d.EmpresaId == centro.EmpresaId)
            .OrderBy(d => d.CreadoEnUtc).ThenBy(d => d.Id)
            .Take(5)
            .ToListAsync(cancellationToken);
        if (documentosEmpresa.Count < 5)
            return;

        dbContext.AcreditacionesDocumentoPlataforma.Add(
            new AcreditacionDocumentoPlataforma(documentosEmpresa[0].Id, canal.Id));

        var subida = new AcreditacionDocumentoPlataforma(documentosEmpresa[1].Id, canal.Id);
        subida.MarcarSubida();
        dbContext.AcreditacionesDocumentoPlataforma.Add(subida);

        var aceptada = new AcreditacionDocumentoPlataforma(documentosEmpresa[2].Id, canal.Id);
        aceptada.MarcarSubida();
        aceptada.MarcarAceptada();
        dbContext.AcreditacionesDocumentoPlataforma.Add(aceptada);

        var noRequerida = new AcreditacionDocumentoPlataforma(documentosEmpresa[3].Id, canal.Id);
        noRequerida.MarcarNoRequerida();
        dbContext.AcreditacionesDocumentoPlataforma.Add(noRequerida);

        var rechazada = new AcreditacionDocumentoPlataforma(documentosEmpresa[4].Id, canal.Id);
        rechazada.MarcarSubida();
        rechazada.Rechazar(CausaRechazoAcreditacion.Ilegible,
            "El PDF llega escaneado torcido y no se distingue el sello.", DateTime.UtcNow.AddDays(-10));
        rechazada.Rechazar(CausaRechazoAcreditacion.CaducadoAlPresentar,
            "El certificado presentado venció el mes pasado.", DateTime.UtcNow.AddDays(-2));
        dbContext.AcreditacionesDocumentoPlataforma.Add(rechazada);
    }
}
