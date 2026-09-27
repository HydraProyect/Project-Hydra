using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Migraciones;

/// <summary>
/// Paso de una base con el historial de migraciones anterior a la compactación
/// (P1-M3) a la línea base <see cref="IdLineaBase"/>, sin tocar el esquema.
///
/// <para>
/// La línea base crea desde cero exactamente el mismo esquema que dejaban las
/// <see cref="IdsHistoriaPrevia"/> (185 migraciones, de
/// <c>20260731235023_LineaBase</c> a
/// <c>20260926133410_CorregirBackfillReclamacionBuzonIntegracionBajoRls</c>).
/// Una base que ya las tiene aplicadas no debe ejecutarla: solo cambia su
/// <c>__EFMigrationsHistory</c>, las 185 filas por la de la línea base. Lo hace
/// el propio migrador antes de <c>MigrateAsync</c>, igual en staging,
/// producción y desarrollo, para que fusionar y desplegar no dependan de un
/// paso manual previo.
/// </para>
///
/// <para>
/// Guarda: solo actúa si el historial contiene EXACTAMENTE el conjunto de las
/// 185, en cualquier orden, y no contiene la línea base. Sin tabla de historial
/// o con ella vacía (base nueva) no interviene y EF aplica la línea base. Con la
/// línea base y sin ninguna previa no hace nada (ya transicionada; relanzar el
/// migrador es inocuo). Cualquier otro estado aborta sin tocar nada y nombra
/// los ids que faltan o sobran. Todo ocurre en una transacción, bajo un cerrojo
/// consultivo y con la tabla de historial bloqueada, así que dos migradores a
/// la vez no pueden transicionar dos veces ni cruzarse con el cerrojo de
/// migración de EF.
/// </para>
///
/// <para>
/// Se puede retirar, junto con la lista, cuando staging, producción y las bases
/// de desarrollo hayan pasado por aquí.
/// </para>
/// </summary>
public static class TransicionLineaBaseCompactada
{
    /// <summary>Id de la migración de línea base compactada.</summary>
    public const string IdLineaBase = "20260926160042_LineaBaseCompactada";

    /// <summary>Las 185 migraciones que la línea base sustituye.</summary>
    public static readonly IReadOnlyList<string> IdsHistoriaPrevia =
    [
        "20260731235023_LineaBase",
        "20260801120000_HabilitarRlsPostgres",
        "20260801130000_AgregarTrabajosAnalisisDocumento",
        "20260801161736_IndiceTenantIdAspNetUsers",
        "20260801182947_CrearPreferenciaDashboardUsuario",
        "20260801211346_AgregarClavesForaneasNucleoCae",
        "20260801223000_RendimientoBusquedasYCheckXorDocumento",
        "20260802101112_CrearClaveApi",
        "20260802111244_CrearFiltroGuardado",
        "20260802153427_CrearIntegracionesMicrosoft365",
        "20260802165602_HabilitarRlsClavesApi",
        "20260802191958_HabilitarRlsIntegraciones",
        "20260802232925_AgregarCifASubcontrata",
        "20260803012741_AgregarAdjuntosMensajeCorreo",
        "20260803012854_HabilitarRlsAdjuntosMensajeCorreo",
        "20260803020328_AgregarSugerenciasVisitaCorreo",
        "20260803020346_HabilitarRlsSugerenciasVisitaCorreo",
        "20260803021846_AgregarOrigenVisita",
        "20260803190523_AgregarArchivoARequisitoDocumental",
        "20260803193417_AgregarGestionesYSugerenciaGestion",
        "20260803193509_HabilitarRlsGestiones",
        "20260803201953_AgregarCumplidoARequisitoDocumental",
        "20260804004148_AgregarWhatsAppComunicaciones",
        "20260804004307_HabilitarRlsWhatsApp",
        "20260805083826_AgregarUmbralesVisitaAParametroSistema",
        "20260805091647_AgregarSolicitudPrioridadDocumento",
        "20260805091735_HabilitarRlsSolicitudPrioridadDocumento",
        "20260805224615_RetirarEvaluaciones",
        "20260806012330_RetirarRequisitoDocumental",
        "20260806172723_CanalesGestionDocumentalNPorCentro",
        "20260806230602_AgregarValidacionDocumentoOficial",
        "20260807165146_AgregarCatalogoProveedoresPlataformaCae",
        "20260807173202_MigrarCanalGestionAProveedorPlataformaCae",
        "20260807201405_RenombrarConversacionMensaje",
        "20260807210931_AgregarCanalAMensaje",
        "20260807231519_AgregarEventosConversacion",
        "20260807234142_HabilitarRlsEventosConversacion",
        "20260808101851_AgregarAcreditacionDocumentoPlataforma",
        "20260808101926_HabilitarRlsAcreditacionDocumentoPlataforma",
        "20260808122021_AgregarAceptacionTerminos",
        "20260808131828_AgregarReclamacionDocumental",
        "20260808131858_HabilitarRlsReclamacionDocumental",
        "20260809122633_ObligatoriosCatalogoTrabajadorCae",
        "20260809152455_ModalidadPreventivaObligatoriaCae",
        "20260809201159_ArreglarIndiceAsignacionesActivas",
        "20260809222134_AgregarConfianzaSugerencias",
        "20260809232418_AgregarConfianzaPorCampoSugerencias",
        "20260810060229_AgregarUltimaActividadUsuario",
        "20260810094939_AgregarClasificacionRuidoMensaje",
        "20260810095001_HabilitarRlsClasificacionRuidoMensaje",
        "20260810122003_RediseñarSugerenciaGestionCorreoMultiItem",
        "20260810122048_HabilitarRlsDetalleSugerenciaGestionCorreo",
        "20260810124408_AgregarClasificacionRuidoDetalleGestion",
        "20260810124429_HabilitarRlsClasificacionRuidoDetalleGestion",
        "20260810135628_AgregarUltimoResumenNotificacionPlataforma",
        "20260810135658_HabilitarRlsUltimoResumenNotificacionPlataforma",
        "20260810150607_AgregarClasificacionRelevanciaCae",
        "20260810150626_HabilitarRlsClasificacionRelevanciaCae",
        "20260810153341_AgregarGestorPropietarioIdAConexionIntegracion",
        "20260810164919_VencimientoAnualDocumentosVehiculo",
        "20260810175536_AgregarNivelServicioYVerificacionExternaSubcontrata",
        "20260810175556_HabilitarRlsVerificacionesExternaSubcontrata",
        "20260810195520_AgregarConfiguracionOperativaAParametroSistema",
        "20260810200402_AgregarAntelacionAVisita",
        "20260810201059_AgregarResolucionSugerencias",
        "20260810201340_AgregarRegistroTiempoGestion",
        "20260810201425_HabilitarRlsRegistroTiempoGestion",
        "20260812214428_AgregarConversacionAReclamacionDocumental",
        "20260813073936_AgregarAgendaContactosYTelefonoTrabajador",
        "20260814002142_AgregarPuestoATrabajador",
        "20260814071159_AgregarCnaeConvenioYAnexoIAEmpresa",
        "20260814072640_AgregarTiposDocumentoTramo0Formatos",
        "20260814075546_AgregarTiposDocumentoTramo1Formatos",
        "20260814082243_AgregarAvisosRevisionNormativa",
        "20260814085521_AgregarPerfilVocabularioATenant",
        "20260814220231_IndicesTrigramFuncionalesSobreUpper",
        "20260814223736_AgregarPresupuestoMensualIaParametroSistema",
        "20260814224730_AgregarEstadoComercialATenant",
        "20260815001950_AgregarEnlaceDocumentoYDecisionHumanaAuditoriaIa",
        "20260815120910_AgregarFirmaEnCampoDocumento",
        "20260815123715_AgregarFirmaGuardadaUsuarioYSelloEmpresa",
        "20260815170155_AgregarContactoAgendaRol",
        "20260815171226_AgregarPlantillasDocumento",
        "20260815171720_AgregarNombreCampoAcroFormAPlantillaElemento",
        "20260815175107_AgregarConocimientoDeteccionCampo",
        "20260815184432_AgregarTipoDocumentoYDocumentoGenerado",
        "20260815193115_AgregarLoteGeneracionDocumento",
        "20260816230513_AgregarEstadoAutomatizacion",
        "20260816233650_HabilitarRlsEstadosAutomatizacion",
        "20260816235025_AgregarHistorialImportacion",
        "20260817003747_AgregarHistorialInforme",
        "20260818092146_AgregarAsignacionesOperativas",
        "20260820131158_AuditoriaConIdentidadDual",
        "20260820143207_ModeloPrivilegioPlataforma",
        "20260820212845_RolSoporteSoloLectura",
        "20260820230549_RlsCatalogosDeAsignacion",
        "20260821150319_RlsPlanoPrivilegioPlataforma",
        "20260822163546_EstadoBootstrapPlataforma",
        "20260825153829_F3aEmpresasUnificadaPreparacion",
        "20260825203823_F3bClienteRepunteoFks",
        "20260826082830_F3bSubcontrataRepunteoFks",
        "20260826153714_AgregarRelacionEmpresarial",
        "20260827144113_F4CierreDropTablasPuente",
        "20260827221301_PartirEsObligatorioEnRequeridoYNaturaleza",
        "20260827230159_AgregarSolicitudCertificacionTgss",
        "20260827232108_HabilitarRlsSolicitudCertificacionTgss",
        "20260827234143_CorregirRequeridoCatalogoT2",
        "20260828001152_AgregarDatosDemoCompletadosATenant",
        "20260828023638_AgregarTipoDocumentoAlias",
        "20260828171712_F3cRetiradaClientesSubcontratasLegacy",
        "20260829003334_AgregarEventoRecienteUsuario",
        "20260829003926_RenombrarTiposDocumentoContaminadosT3",
        "20260830132011_AgregarSiguienteIntentoATrabajoAnalisisDocumento",
        "20260830132455_AcotarResponsableClienteAGlobalVigente",
        "20260830132641_ReemplazarProcesadoPorEstadoEnEventoWebhook",
        "20260830133725_AgregarVersionAItemGeneracionDocumento",
        "20260830134452_AgregarSolicitudConexionMicrosoft365",
        "20260830134636_FkCompuestaConTenantEnLineaWhatsAppConexionIntegracion",
        "20260830135800_IndicesParametroSistemaYEventoWebhookPendientes",
        "20260830140949_IndicesTenantPrimeroEnRegistroAuditoria",
        "20260830142434_AcotarHiloExternoIdPorConexion",
        "20260830143221_AgregarVersionACredencialIntegracion",
        "20260830143331_ClaveCompletaEnExtraccionIaCache",
        "20260830143809_AcotarActivoUnicoProyectoTecnico",
        "20260830144731_AcotarDniUnicoTrasAnonimizar",
        "20260830153712_HuecosArquitectonicosModulo5",
        "20260830163637_CachearAccessTokenCredencialIntegracion",
        "20260830165153_RetencionPayloadEventoWebhook",
        "20260830221006_EliminarIndicePlanoFechaVencimientoDocumento",
        "20260830224058_ReproducibilidadAuditoriaExtraccionIa",
        "20260830233728_FkCompuestaTenantComunicaciones",
        "20260902064822_AgregarVersionAAvisoRevisionNormativa",
        "20260902140700_ReclamacionYConversacionConTitularEmpresa",
        "20260902173155_AlinearModeloConCheckXorDocumentoExistente",
        "20260902174316_FkTenantEmpresaEnConversacion",
        "20260903034630_AgregarDetalleEjecucionAAutomatizacion",
        "20260903034815_AgregarSensibilidadDocumental",
        "20260903035055_SolapeDeVigenciasEnAsignaciones",
        "20260903040338_AgregarOperacionImportacion",
        "20260903090006_RetirarAgregadoAlerta",
        "20260903192717_AgregarExtraccionIaCacheDocumento",
        "20260903192804_HabilitarRlsExtraccionIaCacheDocumento",
        "20260903193339_AgregarRegistroAccesoDocumentoSensible",
        "20260903193429_AgregarPermisoConsultarAccesoDocumentosSensibles",
        "20260903193514_HabilitarRlsRegistrosAccesoDocumentoSensible",
        "20260903202107_BackfillVinculosExtraccionIaCacheDesdeAuditoria",
        "20260904002619_AgregarInstruccionTratamientoIaTenantPropietario",
        "20260905182446_AgrupadorXorRegistroActividadSoporte",
        "20260910233636_ResultadoEjecucionPurgaEIncidencias",
        "20260910233755_HabilitarRlsIncidenciasPurga",
        "20260914001107_VincularRevisionIaAAuditoriaExtraccion",
        "20260916194412_RolAprovisionamientoEscrituraAcotada",
        "20260916203428_RlsConcesionPorAdminDePlataforma",
        "20260917153550_RenombrarPropositoDelegacionComercial",
        "20260919175651_TipoActorEnRegistrosDeAuditoria",
        "20260921010722_VigenciaDeAcreditacionPorPlataforma",
        "20260921155801_ResolucionDeClaveApiBajoRls",
        "20260922174403_AgregarIdiomaPreferidoAUsuario",
        "20260922201144_SolicitudesIncorporacionCartera",
        "20260922234157_AgregarReclamacionBuzonIntegracion",
        "20260923040243_AgregarOrdenMenuLateral",
        "20260923132107_BackfillReclamacionBuzonIntegracionDesdeConexionesExistentes",
        "20260923134000_AgregarNotasInternasConversacion",
        "20260923134038_HabilitarRlsNotasInternasConversacion",
        "20260923134849_AgregarCapacidadOperadorCaeExternoATenant",
        "20260923234946_RevocarAsignacionesOperadorDelegadoConRolDePropiedad",
        "20260924003717_SepararVigenciaDocumentoSinConfirmar",
        "20260924003759_RetirarDniDeRecientesDeTrabajador",
        "20260924142034_AgregarTareasAsistente",
        "20260924143203_HabilitarRlsTareasAsistente",
        "20260924150000_ContextoRlsFirmado",
        "20260925024247_AuditoriaSoloInsercionParaRuntime",
        "20260925041840_AgregarGestionCaeCentro",
        "20260925131737_SoporteLecturaGlobalConCheckDeAlcance",
        "20260925164459_AccesosSoporteTalvegVisiblesParaAdministrador",
        "20260925183417_CapacidadEnSesionPrivilegiada",
        "20260925184521_RlsAspNetUsers",
        "20260925191959_IndicesRamasLecturaAspNetUsers",
        "20260925203337_RestablecimientoSegundoFactorPorSoporte",
        "20260926032940_SerializarAdministradorUnicoEnRestablecimiento",
        "20260926033447_VisitaCanceladaReversible",
        "20260926035507_ParticionarAuditoriaPorMes",
        "20260926081608_ActividadSoporteSoloInsercionParaRuntime",
        "20260926121120_VersionEncoladaYDescarteEnTrabajosAnalisis",
        "20260926133410_CorregirBackfillReclamacionBuzonIntegracionBajoRls",
    ];

    /// <summary>
    /// Aviso operativo (registro y alerta) cuando la transición se aplica: tiene
    /// que verse en Seq y en Sentry, porque ocurre una sola vez por base.
    /// </summary>
    public static string MensajeAplicada =>
        $"transición de línea base aplicada: {IdsHistoriaPrevia.Count} migraciones previas sustituidas por "
        + $"{IdLineaBase} en __EFMigrationsHistory";

    /// <summary>
    /// Aplica la transición si procede. <paramref name="versionProducto"/> es
    /// la versión de EF Core que se anota en la fila de la línea base, la misma
    /// que escribiría EF al aplicarla.
    /// </summary>
    /// <exception cref="TransicionLineaBaseAbortadaException">
    /// El historial no es ni el previo completo, ni el ya transicionado, ni
    /// vacío. No se ha modificado nada.
    /// </exception>
    public static async Task<ResultadoTransicionLineaBase> AplicarAsync(
        string cadenaPropietaria, string versionProducto, CancellationToken ct)
    {
        await using var conexion = new NpgsqlConnection(cadenaPropietaria);
        try
        {
            await conexion.OpenAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            // La base aún no existe (desarrollo, base nueva): la crea
            // MigrateAsync, y no tiene historial que transicionar.
            return ResultadoTransicionLineaBase.BaseSinHistoria;
        }

        await using var transaccion = await conexion.BeginTransactionAsync(ct);

        await using (var cerrojo = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('transicion_linea_base_compactada', 0))",
            conexion, transaccion))
            await cerrojo.ExecuteNonQueryAsync(ct);

        await using (var existe = new NpgsqlCommand(
            "SELECT to_regclass('\"__EFMigrationsHistory\"') IS NOT NULL", conexion, transaccion))
        {
            if (!(bool)(await existe.ExecuteScalarAsync(ct))!)
            {
                await transaccion.CommitAsync(ct);
                return ResultadoTransicionLineaBase.BaseSinHistoria;
            }
        }

        // El mismo bloqueo que toma EF al migrar: nadie aplica ni lee
        // migraciones mientras se decide y se reescribe el historial.
        await using (var bloqueo = new NpgsqlCommand(
            "LOCK TABLE \"__EFMigrationsHistory\" IN ACCESS EXCLUSIVE MODE", conexion, transaccion))
            await bloqueo.ExecuteNonQueryAsync(ct);

        var aplicadas = new HashSet<string>(StringComparer.Ordinal);
        await using (var lectura = new NpgsqlCommand(
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"", conexion, transaccion))
        await using (var filas = await lectura.ExecuteReaderAsync(ct))
        {
            while (await filas.ReadAsync(ct))
                aplicadas.Add(filas.GetString(0));
        }

        var resultado = Decidir(aplicadas);
        if (resultado is not ResultadoTransicionLineaBase.Aplicada)
        {
            await transaccion.CommitAsync(ct);
            return resultado;
        }

        await using (var borrado = new NpgsqlCommand(
            "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = ANY(@previas)", conexion, transaccion))
        {
            borrado.Parameters.AddWithValue("previas", IdsHistoriaPrevia.ToArray());
            var borradas = await borrado.ExecuteNonQueryAsync(ct);
            if (borradas != IdsHistoriaPrevia.Count)
                throw new InvalidOperationException(
                    $"Transición de línea base: se esperaban {IdsHistoriaPrevia.Count} filas y se borraron {borradas}.");
        }

        await using (var alta = new NpgsqlCommand(
            "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES (@id, @version)",
            conexion, transaccion))
        {
            alta.Parameters.AddWithValue("id", IdLineaBase);
            alta.Parameters.AddWithValue("version", versionProducto);
            await alta.ExecuteNonQueryAsync(ct);
        }

        await transaccion.CommitAsync(ct);
        return ResultadoTransicionLineaBase.Aplicada;
    }

    /// <summary>
    /// Decide sobre el conjunto de ids aplicados, sin tocar la base. Lanza
    /// <see cref="TransicionLineaBaseAbortadaException"/> para todo estado que
    /// no sea uno de los tres reconocidos.
    /// </summary>
    public static ResultadoTransicionLineaBase Decidir(IReadOnlySet<string> aplicadas)
    {
        if (aplicadas.Count == 0)
            return ResultadoTransicionLineaBase.BaseSinHistoria;

        var previasPresentes = IdsHistoriaPrevia.Where(aplicadas.Contains).ToList();

        if (aplicadas.Contains(IdLineaBase))
        {
            if (previasPresentes.Count == 0)
                return ResultadoTransicionLineaBase.YaTransicionada;

            // Línea base mezclada con historia previa: nadie debería haberla
            // dejado así, y no hay forma segura de saber qué se aplicó.
            throw new TransicionLineaBaseAbortadaException(faltan: [], sobran: previasPresentes);
        }

        var faltan = IdsHistoriaPrevia.Where(id => !aplicadas.Contains(id)).ToList();
        var sobran = aplicadas.Where(id => !IdsHistoriaPrevia.Contains(id)).Order(StringComparer.Ordinal).ToList();
        if (faltan.Count == 0 && sobran.Count == 0)
            return ResultadoTransicionLineaBase.Aplicada;

        throw new TransicionLineaBaseAbortadaException(faltan, sobran);
    }
}

/// <summary>Qué hizo (o hará) la transición a la línea base compactada.</summary>
public enum ResultadoTransicionLineaBase
{
    /// <summary>Base sin historial (nueva): EF aplica la línea base.</summary>
    BaseSinHistoria,

    /// <summary>Ya tenía la línea base y ninguna previa: nada que hacer.</summary>
    YaTransicionada,

    /// <summary>Las 185 previas se sustituyen por la línea base.</summary>
    Aplicada,
}

/// <summary>
/// El historial de migraciones no está en ninguno de los estados que la
/// transición reconoce. No se ha modificado nada.
/// </summary>
public sealed class TransicionLineaBaseAbortadaException(
    IReadOnlyList<string> faltan, IReadOnlyList<string> sobran)
    : InvalidOperationException(
        "Transición de línea base abortada: el historial de migraciones no es el previo completo "
        + $"({TransicionLineaBaseCompactada.IdsHistoriaPrevia.Count} ids), ni el ya transicionado, ni vacío. "
        + $"Faltan: [{string.Join(", ", faltan)}]. Sobran: [{string.Join(", ", sobran)}]. No se ha modificado nada.")
{
    /// <summary>Ids previos que faltan en el historial.</summary>
    public IReadOnlyList<string> Faltan { get; } = faltan;

    /// <summary>Ids presentes que el estado reconocido no admite.</summary>
    public IReadOnlyList<string> Sobran { get; } = sobran;
}
