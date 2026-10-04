using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// S4 (coherencia entre superficies): impide reintroducir la copia de las reglas de negocio que ya viven en un
/// punto único. La misma regla calculada distinto en dos pantallas causó D-13, D-17, D-22 y los defectos de la
/// ventana de reclamación (#1026, #1028). Cada regla inventariada tiene aquí su patrón, su punto único y la
/// lista de ubicaciones que todavía la repiten, CADA UNA con su motivo; una ubicación nueva es rojo.
///
/// <para>
/// Es la mitad barata de la garantía: impide una copia nueva. La otra mitad, que las superficies existentes den
/// el mismo resultado para la misma entrada, la prueban las tablas de
/// <c>CaeManager.IntegrationTests/Coherencia</c> (estado de vigencia y ventana de reclamación) y
/// <c>CoherenciaDeLaSeveridadDelEstadoTests</c> (Domain.Tests). Mismo mecanismo de ratchet por texto que
/// <see cref="DiaDeNegocioUnicaFuenteTests"/>: una expresión no es una dependencia de tipo, la reflexión no la ve.
/// </para>
///
/// <para>
/// Reglas vigiladas:
/// <list type="number">
/// <item><b>Ventana de reclamación</b> — punto único <c>VentanaReclamacion</c> (Application/Reclamaciones). Cero
/// copias del literal de 3 meses ni de su variable local <c>limiteVentana</c>.</item>
/// <item><b>Orden de gravedad de un estado documental</b> — punto único <c>SeveridadEstadoDocumento.Rango</c>
/// (Domain). Un orden nuevo escrito a mano (<c>Faltante =&gt; 0</c>, <c>[Vencido] = 1</c>…) es rojo salvo en las
/// excepciones declaradas, que responden a otra pregunta.</item>
/// <item><b>Umbrales de vigencia en SQL</b> — punto único <c>CalculadoraEstadoDocumento.Calcular</c> (Domain). EF
/// no puede llamar a la calculadora dentro de una consulta, así que unas pocas consultas reimplementan
/// <c>hoy + umbral</c> para filtrar o acotar en PostgreSQL: están enumeradas y cada una la ata a la calculadora la
/// tabla de <c>CoherenciaDelEstadoDeVigenciaEntreSuperficiesTests</c>. Una consulta nueva que lo necesite se añade
/// aquí a conciencia, con su motivo, y a esa tabla.</item>
/// <item><b>Qué documentos cuentan como «al día» en un porcentaje</b> — punto único
/// <c>CumplimientoDocumental.EsConforme</c> (Domain) y <c>FraccionCumplimiento</c>. Un conjunto de estados «al día»
/// escrito a mano (<c>Vigente or SinCaducidad</c>…) o un <c>Count(… == Vigente)</c> es rojo salvo en las excepciones
/// declaradas, que responden a otra pregunta (incidencia, estado de un propietario, idoneidad estricta para una
/// visita).</item>
/// <item><b>Bloqueo de acceso por documento bloqueante</b> — punto único <c>ReglaBloqueoDeAcceso</c> (Domain). Los
/// lectores de la fila bloqueante del Centro están enumerados con su motivo y la comparación «sin fecha o fecha &gt;= hoy»
/// no puede reaparecer en una consulta; la tabla es <c>CoherenciaDelBloqueoDeAccesoEntreSuperficiesTests</c>.</item>
/// <item><b>Qué documentos están en uso</b> — punto único <c>DocumentoOperativo.Expresion</c> (Domain): no eliminado y no
/// sustituido. Comparar <c>SustituidoEnUtc</c> o <c>SustituidoPorDocumentoId</c> con null a mano es rojo.</item>
/// <item><b>Lectores de Documentos solo sobre operativos</b> — <c>documentosContext.Documentos.Operativos()</c>. Todo
/// lector que no lo aplica (listados con historial, auditoría, purga, resolución por Id, actividad) está inventariado con
/// su motivo; uno nuevo es rojo.</item>
/// <item><b>Elegir el documento efectivo</b> — punto único <c>DocumentoEfectivo</c> (Application): válido hoy → nominativo
/// → emisión más reciente → vigencia confirmada → <c>CreadoEnUtc</c> → <c>Id</c>. Un orden de elección escrito a mano
/// (por «sin confirmar», por <c>FechaEmision</c> descendente o por quien vence más tarde) es rojo salvo en las
/// excepciones declaradas.</item>
/// </list>
/// </para>
/// </summary>
public class ReglasDeNegocioSinCopiasTests
{
    // ---------- 1. Ventana de reclamación ----------

    private const string PuntoUnicoDeLaVentana = "src/CaeManager.Application/Reclamaciones/VentanaReclamacion.cs";

    /// <summary>
    /// El literal de 3 meses, la variable local que lo copiaba y el uso del límite fuera del punto único: quien
    /// necesite la condición sobre un Documento usa <c>Reclamables(hoy)</c> o <c>EsReclamable(fecha, hoy)</c>, no
    /// <c>Limite(hoy)</c> con su propia comparación.
    /// </summary>
    private static readonly Regex PatronVentanaCopiada = new(
        @"\bAddMonths\s*\(\s*\+?3\s*\)|\blimiteVentana\b|\bVentanaReclamacion\s*\.\s*(?:Limite\s*\(|Meses\b)",
        RegexOptions.Compiled);

    /// <summary>Usos de esas formas que no son la ventana de reclamación.</summary>
    private static readonly Dictionary<string, int> VentanaNoEsLaDeReclamacion = new()
    {
        // Siembra de DATOS DE DEMOSTRACIÓN (SembrarReclamacionesAsync): elige documentos «a punto de entrar en
        // ventana» con una aproximación propia de 90 días (hoy, hoy + 90) para dejar reclamaciones de ejemplo. No lee
        // ParametroSistema ni afecta a lo que una superficie ofrece o acepta; la detecta el nombre de la variable, no
        // el significado (si se renombra, deja de verse). Declarada como excepción a conciencia.
        ["src/CaeManager.Infrastructure/Persistence/Seed/DatosPruebaSeeder.cs"] = 2,
    };

    [Fact]
    public void La_ventana_de_reclamacion_no_se_copia_fuera_de_su_punto_unico()
    {
        var medidos = ContarPorFichero(PatronVentanaCopiada);

        medidos.ContainsKey(PuntoUnicoDeLaVentana).Should().BeFalse(
            "el punto único ni siquiera repite el literal: define Meses y deriva el resto de él");

        Divergencias(VentanaNoEsLaDeReclamacion, medidos).Should().BeEmpty(
            "el límite de lo reclamable es VentanaReclamacion.Limite(hoy) y la condición sobre un Documento es " +
            "documentos.Reclamables(hoy) o VentanaReclamacion.EsReclamable(fecha, hoy); copiar el literal de 3 meses " +
            "fue lo que hizo que el envío rechazara lo que la ficha ofrecía (#1026, #1028)");
    }

    // ---------- 2. Orden de gravedad ----------

    /// <summary>
    /// Un orden de gravedad escrito a mano: la entrada de un <c>switch</c>, de un diccionario o de un inicializador
    /// que da un número a un estado documental, un <c>case X: return n</c>, o una partición ternaria
    /// <c>Estado == X ? 0 : 1</c>. No ve una reimplementación sin números (un árbol de decisiones): esas se
    /// enumeran a mano en <see cref="OrdenesQueElPatronNoVe"/>.
    /// </summary>
    private static readonly Regex PatronOrdenDeGravedadCopiado = new(
        @"\bEstadoDocumento\s*\.\s*(?:Faltante|Vencido|Urgente|Proximo|SinConfirmar|Vigente|SinCaducidad)\s*=>\s*\d"
        + @"|\[\s*EstadoDocumento\s*\.\s*\w+\s*\]\s*=\s*\d"
        + @"|\{\s*EstadoDocumento\s*\.\s*\w+\s*,\s*\d+\s*\}"
        + @"|\bcase\s+EstadoDocumento\s*\.\s*\w+\s*:\s*return\s+\d"
        + @"|\bEstado\s*==\s*EstadoDocumento\s*\.\s*(?:Faltante|Vencido|Urgente|Proximo)\s*\?\s*\d+\s*:",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, int> OrdenDeGravedadDeOtraPregunta = new()
    {
        // El punto único: el rango de gravedad de cada estado (siete entradas, una por valor del enum).
        ["src/CaeManager.Domain/Documentos/SeveridadEstadoDocumento.cs"] = 7,
        // Ordena PROPIETARIOS (Trabajador, Empresa, Vehículo) por su peor estado: ahí Faltante no existe y «sin
        // documentos» va al final. Otra pregunta, otro orden.
        ["src/CaeManager.Application/Documentos/EstadoDocumentalFiltro.cs"] = 6,
        // Prioridad de un Cliente según sus Alertas, con la conversión inversa de número a estado; solo existen los
        // estados que una Alerta emite.
        ["src/CaeManager.Application/Clientes/Queries/ObtenerClientes/ObtenerClientesQuery.cs"] = 4,
        // Informe de vigencia (PDF/Excel): Vigente y SinCaducidad empatan y se ordenan por fecha; con el rango común
        // SinCaducidad pasaría detrás de todo lo vigente y cambiaría el orden del informe.
        ["src/CaeManager.Application/Reportes/Queries/GenerarInformeVigenciaQuery.cs"] = 4,
        // Desglose del Dashboard: no ordena por gravedad, parte en dos (vencidos primero, urgentes después) los dos
        // únicos estados que enseña.
        ["src/CaeManager.Application/Dashboard/Queries/ObtenerDesgloseDashboardQuery.cs"] = 1,
        // Orden de los BLOQUES de la pantalla Alertas: Vencido antes que Faltante, por el mockup (Gen 2); también
        // decide la insignia «peor motivo» de cada grupo por tipo, así que ahí la pantalla puede decir «Vencido»
        // mientras la consulta (ObtenerAlertasQuery, que usa el rango común) pone Faltante primero. Divergencia de
        // presentación heredada del mockup, declarada en el informe de S4 como decisión pendiente del propietario.
        ["src/CaeManager.Web/Features/Alertas/Pages/Alertas.razor.cs"] = 4,
    };

    /// <summary>
    /// Reimplementaciones del criterio «lo malo conocido, luego lo desconocido, luego lo bueno» que no son una tabla
    /// de números y el patrón no puede ver. Lista NO exhaustiva (un patrón de texto no enumera lo que no ve): recoge
    /// la que se conoce. <c>CalculoEstadoDocumentalService.PeorEstado</c> (Application/Documentos) DERIVA el estado de
    /// una agregación en SQL (la peor fecha más «hay algún sin confirmar»), no lo ordena, y la ata a la calculadora
    /// <c>CoherenciaDelEstadoDeVigenciaEntreSuperficiesTests</c> para un documento por propietario. Hay además dos
    /// particiones «vencidos primero» escritas como booleano (<c>OrderByDescending(r =&gt; r.Documentos.Any(d =&gt;
    /// d.Estado == Vencido))</c> en <c>ObtenerLoteReclamacionQuery</c> y <c>ObtenerLoteReclamacionEmpresaQuery</c>):
    /// separan un solo estado del resto, no ordenan por gravedad.
    /// </summary>
    private static readonly string[] OrdenesQueElPatronNoVe =
    [
        "src/CaeManager.Application/Documentos/CalculoEstadoDocumentalService.cs",
    ];

    [Fact]
    public void El_orden_de_gravedad_de_un_estado_documental_no_se_copia_fuera_de_su_punto_unico()
    {
        var medidos = ContarPorFichero(PatronOrdenDeGravedadCopiado);

        Divergencias(OrdenDeGravedadDeOtraPregunta, medidos).Should().BeEmpty(
            "lo peor primero es SeveridadEstadoDocumento.Rango(estado); un orden nuevo escrito a mano puede divergir " +
            "del resto sin que nada lo avise. Si responde a otra pregunta, declara la excepción en esta lista");
    }

    // ---------- 3. Umbrales de vigencia en SQL ----------

    private static readonly Regex PatronUmbralDeVigenciaEnSql = new(
        @"\bAddDays\s*\(\s*[A-Za-z_.]*[Uu]mbral(?:Rojo|Ambar)(?:Dias)?\s*\)",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, int> UmbralesDeVigenciaReimplementadosEnSql = new()
    {
        // Prefiltros de cota superior: nada más allá del umbral ámbar puede ser distinto de Vigente, así que se
        // queda en PostgreSQL. La clasificación la sigue haciendo CalculadoraEstadoDocumento.
        ["src/CaeManager.Application/Alertas/Queries/ObtenerAlertas/ObtenerAlertasQuery.cs"] = 1,
        ["src/CaeManager.Application/Centros/CalculoEstadoCentroService.cs"] = 1,
        // Filtros y orden por estado que clasifican EN SQL (limiteRojo y limiteAmbar): la calculadora no cabe en
        // una expresión de EF. Atados a CalculadoraEstadoDocumento por CoherenciaDelEstadoDeVigenciaEntreSuperficiesTests.
        ["src/CaeManager.Application/Documentos/Queries/ObtenerDocumentos/ObtenerDocumentosQuery.cs"] = 2,
        ["src/CaeManager.Application/Empresas/Queries/ObtenerEmpresas/ObtenerEmpresasQuery.cs"] = 2,
        ["src/CaeManager.Application/Trabajadores/Queries/ObtenerTrabajadores/ObtenerTrabajadoresQuery.cs"] = 2,
        ["src/CaeManager.Application/Vehiculos/Queries/ObtenerVehiculos/ObtenerVehiculosQuery.cs"] = 2,
    };

    [Fact]
    public void Los_umbrales_de_vigencia_no_se_reimplementan_en_sql_sin_declararlo()
    {
        var medidos = ContarPorFichero(PatronUmbralDeVigenciaEnSql);

        Divergencias(UmbralesDeVigenciaReimplementadosEnSql, medidos).Should().BeEmpty(
            "el estado de un documento es CalculadoraEstadoDocumento.Calcular; si una consulta necesita clasificar en " +
            "SQL, se declara aquí con su motivo y se añade a la tabla de CoherenciaDelEstadoDeVigenciaEntreSuperficiesTests");
    }

    // ---------- 4. Qué documentos cuentan como «al día» en un porcentaje ----------

    /// <summary>
    /// Un conjunto de estados «al día» escrito a mano: <c>Vigente</c> y <c>SinCaducidad</c> juntos en un <c>or</c> o un
    /// <c>||</c> (en cualquier orden), o un recuento <c>Count(… Estado == EstadoDocumento.Vigente)</c> /
    /// <c>Sum(…)</c>. Decisión del propietario (2026-10-03): lo que cuenta como al día en un porcentaje es
    /// <c>CumplimientoDocumental.EsConforme</c> (Vigente, Próximo, Urgente y Sin caducidad confirmado; Sin confirmar,
    /// Vencido y Faltante no) y el porcentaje es <c>FraccionCumplimiento</c>. No ve un conjunto sin literales
    /// (<c>estado &gt;= x</c>) ni uno repartido en varias líneas: lista NO exhaustiva.
    /// </summary>
    private static readonly Regex PatronConjuntoAlDiaCopiado = new(
        @"\bEstadoDocumento\s*\.\s*Vigente\b[^;]*?(?:\bor\b|\|\|)[^;]*?\bEstadoDocumento\s*\.\s*SinCaducidad\b"
        + @"|\bEstadoDocumento\s*\.\s*SinCaducidad\b[^;]*?(?:\bor\b|\|\|)[^;]*?\bEstadoDocumento\s*\.\s*Vigente\b"
        + @"|\.\s*(?:Count|Sum)\s*\([^;]*\bEstado\w*\s*==\s*EstadoDocumento\s*\.\s*Vigente\b",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, int> ConjuntoAlDiaDeOtraPregunta = new()
    {
        // El punto único: EsConforme enumera Vigente, Próximo, Urgente y Sin caducidad (una vez).
        ["src/CaeManager.Domain/Documentos/CumplimientoDocumental.cs"] = 1,
        // «Aguantará hasta el final de la visita»: recalcula un documento Vigente a la fecha de fin de la visita y pregunta
        // si deja de serlo. Es una pregunta de caducidad en una fecha futura, no un porcentaje.
        ["src/CaeManager.Application/Asignaciones/Queries/ObtenerAsignacionesDocumentacionPorCentro/ObtenerAsignacionesDocumentacionPorCentroQuery.cs"] = 1,
        // Derivación del estado de una agregación SQL (peor fecha + «hay algún sin confirmar»): no cuenta nada, devuelve
        // un estado de propietario. Atada a la calculadora por CoherenciaDelEstadoDeVigenciaEntreSuperficiesTests.
        ["src/CaeManager.Application/Documentos/CalculoEstadoDocumentalService.cs"] = 1,
        // Traduce el estado de un documento al indicador de la documentación base del Trabajador (Vigente/Próximo/…):
        // es un mapeo de presentación, no un recuento.
        ["src/CaeManager.Application/Documentos/DocumentacionBase/DocumentacionBaseTrabajador.cs"] = 1,
        // «Sin incidencia de color»: omite de la lista de incidencias los estados que no la son (Vigente, Sin caducidad y
        // Sin confirmar). Es la pregunta de la incidencia, distinta de la del porcentaje (Próximo y Urgente: al día en el
        // porcentaje, incidencia en la lista). No suma nada.
        ["src/CaeManager.Application/Subcontratas/CalculoEstadoSubcontrataService.cs"] = 1,
        // Idoneidad ESTRICTA para una visita: un documento Próximo o Urgente no basta para acceder, así que «vigente» aquí
        // es más exigente que «al día en un porcentaje». Dos usos: documento del Trabajador y de la Empresa.
        ["src/CaeManager.Application/Visitas/Queries/ObtenerVisitas/ObtenerVisitasQuery.cs"] = 2,
        // Filtro «Al día» de la lista de Trabajadores de una Empresa: filtra por el PEOR estado de cada Trabajador
        // (sin incidencia), no calcula un porcentaje. Deuda: el rótulo «Al día» del filtro y el porcentaje de la misma
        // pantalla responden a preguntas distintas; unificarlos es una decisión de producto (no se decidió).
        ["src/CaeManager.Web/Features/Empresas/Pages/EmpresaDetalle.razor.cs"] = 1,
    };

    [Fact]
    public void El_conjunto_de_estados_al_dia_de_un_porcentaje_no_se_copia_fuera_de_su_punto_unico()
    {
        var medidos = ContarPorFichero(PatronConjuntoAlDiaCopiado);

        Divergencias(ConjuntoAlDiaDeOtraPregunta, medidos).Should().BeEmpty(
            "lo que cuenta como al día en un porcentaje es CumplimientoDocumental.EsConforme y la fracción es " +
            "FraccionCumplimiento; un conjunto escrito a mano diverge en silencio (Próximo y Urgente, Sin caducidad, " +
            "Sin confirmar). Si responde a otra pregunta, declara la excepción en esta lista con su motivo");
    }

    // ---------- 5. Bloqueo de acceso por documento bloqueante ----------

    private const string PuntoUnicoDelBloqueoDeAcceso = "src/CaeManager.Domain/Documentos/ReglaBloqueoDeAcceso.cs";

    /// <summary>
    /// Quién LEE la fila bloqueante del Centro (<c>TipoDocumentoCentro.BloqueaAcceso</c>) para decidir algo, por los
    /// nombres con que hoy se le llama en esas lecturas (<c>tc</c> y <c>fila</c>). La regla —ausente o vencido bloquea
    /// igual, sujeto Trabajador o Empresa, evaluada POR CENTRO con su vigencia propia y su tolerancia— es de
    /// <c>ReglaBloqueoDeAcceso</c> y de <c>CalculoBloqueoDeAccesoDeTrabajadores</c>; un lector nuevo que decida por su
    /// cuenta es rojo hasta que se declare aquí y se ate a la tabla de
    /// <c>CoherenciaDelBloqueoDeAccesoEntreSuperficiesTests</c>. Patrón por nombre: no ve una lectura con otro nombre de
    /// variable (limitación de este mecanismo, igual que el de las demás reglas).
    /// </summary>
    private static readonly Regex PatronLecturaDeLaFilaBloqueante = new(
        @"\b(?:tc|fila)\s*\.\s*BloqueaAcceso\b", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> LecturasDeLaFilaBloqueante = new()
    {
        // El único sitio que carga las filas bloqueantes de cada Centro (con su vigencia propia y su tolerancia) y aplica la
        // regla única por Centro (R1, R2): lo usan Mi trabajo y el detalle por Trabajador del Centro 360.
        ["src/CaeManager.Application/Centros/EvaluacionDeAccesoPorCentroService.cs"] = 1,
        // El semáforo del Centro ya no lee BloqueaAcceso: «Bloqueado» es un estado del Trabajador (2026-10-03) y el Centro solo
        // lo está por la plataforma del Cliente empresarial (D-7). Por eso no figura aquí.
    };

    /// <summary>
    /// La comparación «sin fecha O fecha &gt;= hoy» escrita en una consulta o en una pantalla: es la regla de validez
    /// del bloqueo copiada (la tuvo la consulta de Mi trabajo hasta que pasó a <c>ReglaBloqueoDeAcceso</c>).
    /// </summary>
    private static readonly Regex PatronValidezDeBloqueoCopiada = new(
        @"\bFechaVencimiento\s*(?:==\s*null|is\s+null|is\s+not\s*\{\s*\}\s*\w+)\s*\|\|", RegexOptions.Compiled);

    [Fact]
    public void La_regla_de_bloqueo_de_acceso_no_se_decide_fuera_de_sus_lectores_declarados()
    {
        Divergencias(LecturasDeLaFilaBloqueante, ContarPorFichero(PatronLecturaDeLaFilaBloqueante)).Should().BeEmpty(
            "quien lee TipoDocumentoCentro.BloqueaAcceso para decidir un bloqueo tiene que usar ReglaBloqueoDeAcceso; si es " +
            "un lector nuevo, se declara aquí con su motivo y se añade a la tabla de CoherenciaDelBloqueoDeAccesoEntreSuperficiesTests");
    }

    [Fact]
    public void La_validez_de_un_documento_para_bloquear_no_se_copia_fuera_de_su_punto_unico()
    {
        var medidos = ContarPorFichero(PatronValidezDeBloqueoCopiada);

        medidos.Should().BeEmpty(
            "«sin fecha o fecha >= hoy» es ReglaBloqueoDeAcceso.ValidoParaAcceder(...); EF no puede llamarla en una consulta, " +
            "así que se trae el estado y la fecha de vigencia y se evalúa en memoria, no se copia la comparación");
    }

    // ---------- 6. Qué documentos están en uso (operativos) ----------

    private const string PuntoUnicoDelDocumentoOperativo = "src/CaeManager.Domain/Documentos/DocumentoOperativo.cs";

    /// <summary>
    /// La condición «este documento no está sustituido» escrita a mano (<c>SustituidoEnUtc == null</c>,
    /// <c>!= null</c>, <c>is null</c>, <c>is not null</c>). Quien recorre documentos sin un requisito delante filtra con
    /// <c>DocumentoOperativo.Expresion</c> (o <c>DocumentoOperativo.Es</c> en memoria): un filtro escrito a mano se
    /// olvida de la mitad «no eliminado» y es la copia que el diseño del documento efectivo (2026-10-03, § 2.4) prohíbe.
    /// </summary>
    private static readonly Regex PatronSustitucionComparadaAMano = new(
        @"\b(?:SustituidoEnUtc|SustituidoPorDocumentoId)\s*(?:==|!=|is\s+(?:not\s+)?null\b|\.\s*HasValue\b)"
        // El mismo filtro en SQL crudo o en una cadena: "SustituidoEnUtc" IS [NOT] NULL.
        + @"|\\?""SustituidoEnUtc\\?""\s+IS\s+(?:NOT\s+)?NULL\b"
        // La propiedad derivada del agregado usada desde fuera: EF no la traduce (falla en ejecución) y en memoria
        // la pregunta se hace con DocumentoOperativo.Es, que además descarta los eliminados.
        + @"|\b\w+\s*\.\s*EstaSustituido\b",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, int> ComparacionesDeSustitucionDeclaradas = new()
    {
        // El punto único: la expresión operativa.
        [PuntoUnicoDelDocumentoOperativo] = 1,
        // El agregado deriva su propio EstaSustituido de la columna (1) y comprueba el del sustituto con
        // `nuevo.EstaSustituido` dentro de SustituirPor (1): son la propiedad de dominio, no una consulta.
        ["src/CaeManager.Domain/Documentos/Documento.cs"] = 2,
    };

    [Fact]
    public void La_condicion_de_documento_operativo_no_se_escribe_a_mano_fuera_de_su_punto_unico()
    {
        Divergencias(ComparacionesDeSustitucionDeclaradas, ContarPorFichero(PatronSustitucionComparadaAMano)).Should().BeEmpty(
            "«documento operativo» (no eliminado y no sustituido) es DocumentoOperativo.Expresion; comparar SustituidoEnUtc a " +
            "mano en una consulta olvida los eliminados cuando se apaga el filtro global y reparte la regla por las superficies");
    }

    /// <summary>
    /// Una ESCRITURA de las columnas de sustitución (<c>SustituidoEnUtc = …</c>). Los setters son privados, así que solo
    /// el agregado puede escribirlas: la sustitución nace en <c>Documento.SustituirPor</c> (tres asignaciones, una por
    /// columna) y nada la deshace (D5: el sustituido no vuelve a ser operativo). Un método nuevo del agregado que las
    /// reasigne —por ejemplo para ponerlas a null— sube la cuenta y es rojo hasta que se declare a conciencia.
    /// </summary>
    private static readonly Regex PatronEscrituraDeLaSustitucion = new(
        @"\b(?:SustituidoEnUtc|SustituidoPorDocumentoId|MotivoSustitucion)\s*=(?![=>])",
        RegexOptions.Compiled);

    [Fact]
    public void La_sustitucion_de_un_documento_solo_se_escribe_en_SustituirPor()
    {
        Divergencias(new Dictionary<string, int> { ["src/CaeManager.Domain/Documentos/Documento.cs"] = 3 },
                ContarPorFichero(PatronEscrituraDeLaSustitucion)).Should().BeEmpty(
            "las tres columnas de sustitución se asignan una sola vez, dentro de Documento.SustituirPor; reasignarlas en otro " +
            "sitio (o a null) permitiría que un documento sustituido volviera a ser operativo (D5)");
    }

    // ---------- 7. Lectores de Documentos: solo operativos, o excepción con motivo ----------

    /// <summary>
    /// Un lector de la fuente <c>Documentos</c> (<c>IDocumentosQueryContext</c>, parámetro <c>documentosContext</c> o
    /// <c>dbContext</c>) que NO filtra los operativos en la misma línea: ni <c>.Operativos()</c>, ni
    /// <c>.Where(DocumentoOperativo.Expresion)</c>, ni <c>.Reclamables(hoy)</c> ni <c>.SinConfirmarSinFecha()</c> (que lo
    /// aplican por dentro: las dos formas de <c>VentanaReclamacion</c>). Un documento
    /// sustituido es historial (D5): no cuenta para estado, alertas, bloqueo, faltantes, paquete ni cifras. Cada lector
    /// que lo ignora a propósito está en <see cref="LectoresDeDocumentosSinFiltroOperativo"/> con su motivo; uno nuevo
    /// es rojo hasta que filtre o se declare a conciencia.
    /// </summary>
    private static readonly Regex PatronLectorDeDocumentosSinFiltroOperativo = new(
        @"\b\w*(?:[Cc]ontext|[Cc]ontexto|[Dd]b)\s*\.\s*Documentos\b(?!\s*\.\s*(?:Operativos\s*\(|Reclamables\s*\(|SinConfirmarSinFecha\s*\(|Where\s*\(\s*DocumentoOperativo\s*\.\s*Expresion))",
        RegexOptions.Compiled);

    /// <summary>
    /// Siembra de datos de prueba y repositorio por Id: el DbContext como almacén, no como lector de estado. No entran
    /// en el inventario porque no deciden nada de lo que ve el Gestor CAE. Son rutas concretas (no toda la carpeta de
    /// persistencia): un lector nuevo en ella sí cuenta.
    /// </summary>
    private static readonly string[] FueraDelInventarioDeLectores =
    [
        "src/CaeManager.Infrastructure/Persistence/Seed/",
        "src/CaeManager.Infrastructure/Persistence/Repositories/DocumentoRepository.cs",
        // La implementación explícita de IDocumentosQueryContext.Documentos: la definición de la fuente, no un lector.
        "src/CaeManager.Infrastructure/Persistence/CaeManagerDbContext.cs",
    ];

    private static readonly Dictionary<string, int> LectoresDeDocumentosSinFiltroOperativo = new()
    {
        // --- El historial se ve a propósito (PR 9 del diseño: «Incluir historial»). La lista de Documentos
        //     (ObtenerDocumentosQuery) ya muestra solo los operativos desde la PR 4 (Renovar sustituye y el anterior
        //     pasaría a salir duplicado); la búsqueda global sigue sin filtrar hasta la PR 9. ---
        ["src/CaeManager.Application/Documentos/Queries/ObtenerDocumentoPorId/ObtenerDocumentoPorIdQuery.cs"] = 2,
        ["src/CaeManager.Application/BusquedaGlobal/Queries/BuscarGlobal/BuscarGlobalQuery.cs"] = 5,

        // --- Búsqueda por Id de un documento que ya se nombró (el historial referencia a documentos que pudieron
        //     sustituirse: el dato referenciado se resuelve aunque sea historial). ---
        ["src/CaeManager.Application/Comunicaciones/Queries/ObtenerConversacionPorId/ObtenerConversacionPorIdQuery.cs"] = 2,
        ["src/CaeManager.Application/Comunicaciones/Deteccion/ClasificacionRuidoMensajeService.cs"] = 1,
        ["src/CaeManager.Application/Visitas/Antelacion/EvaluadorExpedienteVisitaService.cs"] = 1,
        ["src/CaeManager.Application/Documentos/Commands/RestaurarDocumento/RestaurarDocumentoCommand.cs"] = 1,

        // --- Auditoría: quién accedió a qué documento, sea o no operativo hoy. ---
        ["src/CaeManager.Application/Auditoria/Queries/ObtenerAccesosDocumentosSensiblesQuery.cs"] = 1,
        ["src/CaeManager.Application/Auditoria/Queries/ObtenerAuditoriaQuery.cs"] = 1,
        ["src/CaeManager.Application/Auditoria/RegistroAccesoDocumentoSensibleService.cs"] = 1,

        // --- Verificación y revisión de IA: el análisis tardío de un histórico se registra sin mutarlo (PR 4). ---
        ["src/CaeManager.Application/Documentos/Verificacion/VerificacionIaDocumentoService.cs"] = 1,
        ["src/CaeManager.Application/Documentos/Commands/ResolverRevisionIaDocumento/ResolverRevisionIaDocumentoCommand.cs"] = 1,
        ["src/CaeManager.Application/Documentos/Queries/ObtenerRevisionesIaPendientes/ObtenerRevisionesIaPendientesQuery.cs"] = 1,

        // --- Retención y purga: el RGPD alcanza al historial tanto como a lo operativo. ---
        ["src/CaeManager.Application/Retencion/DeteccionPurgaService.cs"] = 1,
        ["src/CaeManager.Application/Retencion/EjecucionPurgaService.cs"] = 1,

        // --- Actividad, no estado: cuentan documentos SUBIDOS en un periodo (producción y facturación), y una versión
        //     sustituida se subió igual; la ficha del Proyecto cuenta los documentos gestionados en su vida, historial
        //     incluido. ---
        ["src/CaeManager.Application/Proyectos/Queries/ObtenerProyectoPorId/ObtenerProyectoPorIdQuery.cs"] = 1,
        ["src/CaeManager.Application/Dashboard/Queries/ObtenerCatalogoKpisQuery.cs"] = 2,
        ["src/CaeManager.Application/Facturacion/Queries/ObtenerResumenFacturacion/ObtenerResumenFacturacionQuery.cs"] = 2,

        // --- Estadística de aprobación por revisor: pasaron por su mano aunque luego se sustituyeran. ---
        ["src/CaeManager.Application/Dashboard/Queries/ObtenerEstadisticasAprobacionDocumentoQuery.cs"] = 1,
        ["src/CaeManager.Application/Dashboard/Queries/ObtenerPulsoEquipoQuery.cs"] = 1,
    };

    [Fact]
    public void Todo_lector_de_documentos_filtra_los_operativos_o_declara_por_que_no()
    {
        var medido = ContarPorFichero(PatronLectorDeDocumentosSinFiltroOperativo)
            .Where(x => !FueraDelInventarioDeLectores.Any(f => x.Key.StartsWith(f, StringComparison.Ordinal)))
            .ToDictionary(x => x.Key, x => x.Value);

        Divergencias(LectoresDeDocumentosSinFiltroOperativo, medido).Should().BeEmpty(
            "un lector de Documentos decide estado, alertas, bloqueo, faltantes, paquete o cifras sobre los operativos: " +
            "`.Operativos()` (o `.Where(DocumentoOperativo.Expresion)`). Si el lector ignora a propósito el historial " +
            "(auditoría, purga, resolución por Id, actividad), se declara aquí con su motivo");
    }

    // ---------- 8. Elegir el documento efectivo: un solo orden ----------

    /// <summary>
    /// Una elección de «qué documento representa» escrita a mano: ordenar por «sin confirmar», quedarse con la emisión
    /// más reciente (<c>OrderByDescending/ThenByDescending/MaxBy(d =&gt; d.FechaEmision)</c>) o por quien vence más tarde
    /// (<c>OrderByDescending(d =&gt; d.FechaVencimiento ?? …)</c>, el criterio que el diseño retiró). El orden es
    /// <c>DocumentoEfectivo.Ordenar</c> (válido hoy → nominativo → emisión más reciente → vigencia confirmada →
    /// <c>CreadoEnUtc</c> → <c>Id</c>); copiarlo en un lector hace que dos superficies elijan documentos distintos.
    /// </summary>
    private static readonly Regex PatronEleccionDeDocumentoEfectivoCopiada = new(
        @"\b(?:OrderBy|ThenBy)(?:Descending)?\s*\([^;]*\bSinConfirmar\b"
        + @"|\b(?:OrderByDescending|ThenByDescending|MaxBy)\s*\([^;]*\b[Ff]echaEmision\b"
        + @"|\b(?:OrderByDescending|ThenByDescending|MaxBy)\s*\([^;]*\b[Ff]echaVencimiento\b[^;]*\?\?",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, int> EleccionesDeDocumentoDeOtraPregunta = new()
    {
        // El punto único: la emisión más reciente y «sin confirmar» como desempates del orden (dos líneas).
        ["src/CaeManager.Application/Documentos/DocumentoEfectivo.cs"] = 2,
        // Orden de la LISTA de Documentos por la columna que elige el usuario (emisión, dos líneas): no elige a quién
        // representa a un tipo, presenta filas.
        ["src/CaeManager.Application/Documentos/Queries/ObtenerDocumentos/ObtenerDocumentosQuery.cs"] = 2,
    };

    [Fact]
    public void El_orden_que_elige_el_documento_efectivo_no_se_copia_fuera_de_su_punto_unico()
    {
        Divergencias(EleccionesDeDocumentoDeOtraPregunta, ContarPorFichero(PatronEleccionDeDocumentoEfectivoCopiada)).Should().BeEmpty(
            "elegir qué documento representa a un par (tipo + sujeto) es DocumentoEfectivo.Ordenar/UnoPorClave; un orden " +
            "escrito a mano en un lector puede divergir del resto sin que nada lo avise. Si responde a otra pregunta " +
            "(presentar filas), declara la excepción en esta lista");
    }

    [Fact]
    public void El_patron_de_eleccion_del_efectivo_reconoce_las_copias_e_ignora_las_ordenaciones_ajenas()
    {
        string[] copias =
        [
            "            .ThenByDescending(d => d.EstadoVigencia != EstadoVigenciaDocumento.SinConfirmar)",
            "            .OrderBy(d => d.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 1 : 0)",
            "            .OrderByDescending(d => d.FechaEmision)",
            "            .ThenByDescending(x => x.FechaEmision).First()",
            "            .ThenByDescending(fechaEmision)",
            "            .ThenByDescending(d => d.Documento.FechaEmision)",
            "            .ThenByDescending(d => fechaVencimiento(d) ?? DateOnly.MaxValue)",
            "        var uno = grupo.MaxBy(d => d.FechaEmision);",
            "            .OrderByDescending(d => d.FechaVencimiento ?? DateOnly.MaxValue)",
            "            .ThenByDescending(d => d.FechaVencimiento ?? DateOnly.MinValue)",
        ];
        foreach (var linea in copias)
            EsCodigoQueCasa(linea, PatronEleccionDeDocumentoEfectivoCopiada).Should().BeTrue(linea);

        string[] ajenas =
        [
            "            .OrderBy(d => d.FechaVencimiento ?? DateOnly.MaxValue)",
            "            .ThenBy(a => a.FechaVencimiento)",
            "            .OrderByDescending(c => c.CreadoEnUtc)",
            "                : d.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 1 : 2)",
            "            // .OrderByDescending(d => d.FechaEmision)",
            "            .OrderByDescending(v => v.FechaVerificacion)",
        ];
        foreach (var linea in ajenas)
            EsCodigoQueCasa(linea, PatronEleccionDeDocumentoEfectivoCopiada).Should().BeFalse(linea);
    }

    // ---------- Instrumento ----------

    [Fact]
    public void El_patron_de_lector_sin_filtro_operativo_reconoce_los_sin_filtro_e_ignora_los_filtrados()
    {
        string[] sinFiltro =
        [
            "        var existentes = await documentosContext.Documentos",
            "            from documento in documentosContext.Documentos",
            "        var x = documentosContext.Documentos.Where(d => d.EmpresaId == id);",
            "        var y = await dbContext.Documentos.FirstOrDefaultAsync(d => d.Id == id);",
            "            join documento in dbContext.Documentos on a.DocumentoId equals documento.Id",
            "            .Join(documentosContext.Documentos, rd => rd.DocumentoId, d => d.Id,",
            "        var z = documentosContext . Documentos",
            "        var w = await _documentosContext.Documentos.ToListAsync();",
            "        var v = contexto.Documentos.Where(d => d.Id == id);",
            "        var u = db.Documentos.Any();",
            "        var t = documentosContext.Documentos.SinConfirmar.Where(d => d.Id == id);",
        ];
        foreach (var linea in sinFiltro)
            EsCodigoQueCasa(linea, PatronLectorDeDocumentosSinFiltroOperativo).Should().BeTrue(linea);

        string[] filtrados =
        [
            "        var x = await documentosContext.Documentos.Operativos()",
            "            from documento in documentosContext.Documentos.Operativos()",
            "        var y = documentosContext.Documentos.Where(DocumentoOperativo.Expresion)",
            "            from documento in documentosContext.Documentos.Reclamables(hoy)",
            "            from documento in documentosContext.Documentos.SinConfirmarSinFecha()",
            "        // documentosContext.Documentos sin filtro",
            "        /// <c>documentosContext.Documentos</c>",
            "        var n = lote.Documentos.Count;",
            "        var m = request.Documentos.Count;",
        ];
        foreach (var linea in filtrados)
            EsCodigoQueCasa(linea, PatronLectorDeDocumentosSinFiltroOperativo).Should().BeFalse(linea);
    }

    [Fact]
    public void El_patron_de_la_condicion_operativa_reconoce_sus_formas_e_ignora_las_ajenas()
    {
        string[] copiadas =
        [
            "            .Where(d => d.SustituidoEnUtc == null)",
            "        where documento.SustituidoEnUtc != null",
            "        d.SustituidoEnUtc is null && d.EmpresaId == empresaId",
            "        => SustituidoEnUtc is not null;",
            "            .Where(d => d.SustituidoPorDocumentoId == null)",
            "        if (documento.SustituidoPorDocumentoId is not null) continue;",
            "            .Where(d => d.SustituidoEnUtc.HasValue)",
            "            \"SELECT * FROM \\\"Documentos\\\" WHERE \\\"SustituidoEnUtc\\\" IS NULL\"",
            "            \"... \\\"SustituidoEnUtc\\\" IS NOT NULL\"",
            "            .Where(d => !d.EstaSustituido)",
            "        var historia = documentos.Where(documento => documento.EstaSustituido);",
        ];
        foreach (var linea in copiadas)
            EsCodigoQueCasa(linea, PatronSustitucionComparadaAMano).Should().BeTrue(linea);

        string[] ajenas =
        [
            "                \"num_nonnulls(\\\"SustituidoPorDocumentoId\\\", \\\"SustituidoEnUtc\\\", \\\"MotivoSustitucion\\\") IN (0, 3) AND \" +",
            "                \"\\\"SustituidoPorDocumentoId\\\" IS DISTINCT FROM \\\"Id\\\"\");",
            "    public bool EstaSustituido => false;",
            "        if (EstaSustituido)",
            "        SustituidoEnUtc = ahoraUtc;",
            "        SustituidoPorDocumentoId = nuevo.Id;",
            "            .Where(DocumentoOperativo.Expresion)",
            "        // d.SustituidoEnUtc == null",
            "        /// <see cref=\"SustituidoEnUtc\"/> es null si sigue operativo",
            "        var antes = documento.FechaVencimiento == null;",
        ];
        foreach (var linea in ajenas)
            EsCodigoQueCasa(linea, PatronSustitucionComparadaAMano).Should().BeFalse(linea);

        string[] escrituras =
        [
            "        SustituidoEnUtc = ahoraUtc;",
            "        SustituidoEnUtc = null;",
            "        SustituidoPorDocumentoId = nuevo.Id;",
            "        MotivoSustitucion = null;",
        ];
        foreach (var linea in escrituras)
            EsCodigoQueCasa(linea, PatronEscrituraDeLaSustitucion).Should().BeTrue(linea);

        string[] noEscrituras =
        [
            "    public DateTime? SustituidoEnUtc { get; private set; }",
            "        d => !d.EstaEliminado && d.SustituidoEnUtc == null;",
            "    public bool EstaSustituido => SustituidoEnUtc is not null;",
            "        // SustituidoEnUtc = null;",
            "            .Where(d => d.MotivoSustitucion == MotivoSustitucionDocumento.Renovacion)",
        ];
        foreach (var linea in noEscrituras)
            EsCodigoQueCasa(linea, PatronEscrituraDeLaSustitucion).Should().BeFalse(linea);
    }

    [Fact]
    public void Los_patrones_reconocen_las_formas_que_vigilan_e_ignoran_comentarios()
    {
        string[] lecturas =
        [
            "            where tc.Incluido && tc.BloqueaAcceso",
            "                var bloquea = filasPorPar.TryGetValue((tipo.Id, centro), out var fila) && fila.BloqueaAcceso;",
            "            .Where(tc => tc . BloqueaAcceso)",
        ];
        foreach (var linea in lecturas)
            EsCodigoQueCasa(linea, PatronLecturaDeLaFilaBloqueante).Should().BeTrue(linea);

        string[] noLecturas =
        [
            "                request.PeriodicidadEspecialMeses, request.BloqueaAcceso, request.ArchivoUrl",
            "                    BloqueaAcceso: fila?.BloqueaAcceso ?? false,",
            "        /// <see cref=\"TipoDocumentoCentro.BloqueaAcceso\"/>",
            "        // tc.BloqueaAcceso",
        ];
        foreach (var linea in noLecturas)
            EsCodigoQueCasa(linea, PatronLecturaDeLaFilaBloqueante).Should().BeFalse(linea);

        string[] validezCopiada =
        [
            "                && (d.FechaVencimiento == null || d.FechaVencimiento >= DiaDeNegocio.Hoy()))",
            "            .Where(d => d.FechaVencimiento is null || d.FechaVencimiento >= hoy)",
            "        vigencia.FechaVencimiento is not { } fecha || fecha >= hoy;",
        ];
        foreach (var linea in validezCopiada)
            EsCodigoQueCasa(linea, PatronValidezDeBloqueoCopiada).Should().BeTrue(linea);

        string[] noValidezCopiada =
        [
            "            where documento.FechaVencimiento != null && documento.FechaVencimiento <= fechaLimiteCausa",
            "        CalculadoraEstadoDocumento.Calcular(vigencia, hoy, umbralAmbarDias: 0, umbralRojoDias: 0) != EstadoDocumento.Vencido;",
            "        // d.FechaVencimiento == null || d.FechaVencimiento >= hoy",
        ];
        foreach (var linea in noValidezCopiada)
            EsCodigoQueCasa(linea, PatronValidezDeBloqueoCopiada).Should().BeFalse(linea);

        string[] ventana =
        [
            "        var limiteVentana = hoy.AddMonths(3);",
            "            where documento.FechaVencimiento <= limiteVentana",
            "        var limite = fecha.AddMonths( 3 );",
            "        var limite = fecha.AddMonths(+3);",
            "            where documento.FechaVencimiento <= VentanaReclamacion.Limite(hoy)",
            "        var limite = VentanaReclamacion.Limite(hoy);",
            "        var limite = hoy.AddMonths(VentanaReclamacion.Meses);",
        ];
        foreach (var linea in ventana)
            EsCodigoQueCasa(linea, PatronVentanaCopiada).Should().BeTrue(linea);

        string[] noVentana =
        [
            "        var filas = documentosContext.Documentos.Reclamables(hoy);",
            "        var esReclamable = VentanaReclamacion.EsReclamable(fecha, hoy);",
            "        var siguiente = primerDia.AddMonths(1);",
            "        var anterior = inicioMes.AddMonths(-3);",
            "        // antes: hoy.AddMonths(3)",
            "        /// <c>limiteVentana</c> era la copia",
        ];
        foreach (var linea in noVentana)
            EsCodigoQueCasa(linea, PatronVentanaCopiada).Should().BeFalse(linea);

        string[] orden =
        [
            "                EstadoDocumento.Faltante => 0,",
            "        [EstadoDocumento.Vencido] = 1,",
            "                EstadoDocumento.Vencido => 0,",
            "                EstadoDocumento.Proximo => 2,",
            "        { EstadoDocumento.Vencido, 0 },",
            "            case EstadoDocumento.Urgente: return 1;",
            "            .OrderBy(d => d.Estado == EstadoDocumento.Vencido ? 0 : 1)",
        ];
        foreach (var linea in orden)
            EsCodigoQueCasa(linea, PatronOrdenDeGravedadCopiado).Should().BeTrue(linea);

        string[] noOrden =
        [
            "        var rango = SeveridadEstadoDocumento.Rango(estado);",
            "                EstadoDocumento.Vencido => TipoItemBandeja.Vencido,",
            "        EstadoDocumento.Proximo => EstadoIndicadorBase.ProximoAVencer,",
            "        // EstadoDocumento.Proximo => 3,",
            "            .Where(a => a.Estado == EstadoDocumento.Vencido && a.DocumentoId is not null)",
            "        .Where(a => a.Estado is EstadoDocumento.Vencido or EstadoDocumento.Faltante)",
        ];
        foreach (var linea in noOrden)
            EsCodigoQueCasa(linea, PatronOrdenDeGravedadCopiado).Should().BeFalse(linea);

        string[] umbrales =
        [
            "            var limiteRojo = hoy.AddDays(parametros.UmbralRojoDias);",
            "        var fechaLimiteCausa = hoy.AddDays(umbralAmbarDias);",
            "        var limite = hoy.AddDays(parametros.UmbralAmbarDias);",
        ];
        foreach (var linea in umbrales)
            EsCodigoQueCasa(linea, PatronUmbralDeVigenciaEnSql).Should().BeTrue(linea);

        string[] noUmbrales =
        [
            "        var limite = hoy.AddDays(30);",
            "        var limiteAvisoVisita = hoy.AddDays(parametros.HorasAvisoVisita / 24);",
            "        // hoy.AddDays(parametros.UmbralRojoDias)",
        ];
        foreach (var linea in noUmbrales)
            EsCodigoQueCasa(linea, PatronUmbralDeVigenciaEnSql).Should().BeFalse(linea);
    }

    [Fact]
    public void El_patron_del_conjunto_al_dia_reconoce_sus_formas_e_ignora_las_ajenas()
    {
        string[] alDia =
        [
            "        EstadoDocumento.SinCaducidad or EstadoDocumento.Vigente or EstadoDocumento.Proximo or EstadoDocumento.Urgente;",
            "            is EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad);",
            "        => e == EstadoDocumento.Vigente || e == EstadoDocumento.SinCaducidad;",
            "        var alDia = documentos.Count(d => d.Estado == EstadoDocumento.Vigente);",
            "        var n = filas.Sum(f => f.Estado == EstadoDocumento.Vigente ? 1 : 0);",
            "        e is EstadoDocumento.Vigente or EstadoDocumento.Proximo or EstadoDocumento.Urgente or EstadoDocumento.SinCaducidad;",
            "        e is EstadoDocumento.SinCaducidad or EstadoDocumento.Proximo or EstadoDocumento.Vigente;",
        ];
        foreach (var linea in alDia)
            EsCodigoQueCasa(linea, PatronConjuntoAlDiaCopiado).Should().BeTrue(linea);

        string[] noAlDia =
        [
            "        if (estado is EstadoDocumento.Vigente) continue;",
            "        EstadoDocumento.Vencido or EstadoDocumento.Faltante => 0,",
            "        var alDia = CumplimientoDocumental.Evaluar(estados);",
            "        // EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad",
            "        var vencidos = documentos.Count(d => d.Estado == EstadoDocumento.Vencido);",
            "        e is EstadoDocumento.Vigente or EstadoDocumento.Proximo;",
        ];
        foreach (var linea in noAlDia)
            EsCodigoQueCasa(linea, PatronConjuntoAlDiaCopiado).Should().BeFalse(linea);
    }

    [Fact]
    public void Cada_punto_unico_y_cada_excepcion_declarada_existe_y_la_lista_mide_lo_que_dice()
    {
        var raiz = RaizDelRepositorio();

        File.Exists(Path.Combine(raiz, PuntoUnicoDeLaVentana)).Should().BeTrue("el punto único de la ventana tiene que existir");
        File.Exists(Path.Combine(raiz, PuntoUnicoDelBloqueoDeAcceso)).Should().BeTrue("el punto único del bloqueo de acceso tiene que existir");
        File.Exists(Path.Combine(raiz, PuntoUnicoDelDocumentoOperativo)).Should().BeTrue("el punto único del documento operativo tiene que existir");
        foreach (var fuera in FueraDelInventarioDeLectores)
            (Directory.Exists(Path.Combine(raiz, fuera.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)))
                || File.Exists(Path.Combine(raiz, fuera.Replace('/', Path.DirectorySeparatorChar))))
                .Should().BeTrue($"{fuera} queda fuera del inventario de lectores: si se movió, deja de excluir lo que decía");

        foreach (var ruta in LecturasDeLaFilaBloqueante.Keys
                     .Concat(ComparacionesDeSustitucionDeclaradas.Keys)
                     .Concat(LectoresDeDocumentosSinFiltroOperativo.Keys)
                     .Concat(VentanaNoEsLaDeReclamacion.Keys)
                     .Concat(OrdenDeGravedadDeOtraPregunta.Keys)
                     .Concat(UmbralesDeVigenciaReimplementadosEnSql.Keys)
                     .Concat(ConjuntoAlDiaDeOtraPregunta.Keys)
                     .Concat(OrdenesQueElPatronNoVe))
        {
            File.Exists(Path.Combine(raiz, ruta.Replace('/', Path.DirectorySeparatorChar)))
                .Should().BeTrue($"{ruta} está en una lista de excepciones: si se movió o se borró, la lista engaña");
        }
    }

    private static List<string> Divergencias(Dictionary<string, int> esperado, Dictionary<string, int> medido) =>
        esperado.Keys.Union(medido.Keys)
            .Select(ruta => (Ruta: ruta, Esperado: esperado.GetValueOrDefault(ruta), Medido: medido.GetValueOrDefault(ruta)))
            .Where(x => x.Esperado != x.Medido)
            .OrderBy(x => x.Ruta, StringComparer.Ordinal)
            .Select(x => $"{x.Ruta}: esperado {x.Esperado}, medido {x.Medido}")
            .ToList();

    private static bool EsCodigoQueCasa(string linea, Regex patron)
    {
        var contenido = linea.TrimStart();
        if (contenido.StartsWith("//", StringComparison.Ordinal)
            || contenido.StartsWith("*", StringComparison.Ordinal)
            || contenido.StartsWith("/*", StringComparison.Ordinal)
            || contenido.StartsWith("@*", StringComparison.Ordinal)
            || contenido.StartsWith("///", StringComparison.Ordinal))
        {
            return false;
        }

        return patron.IsMatch(linea);
    }

    private static Dictionary<string, int> ContarPorFichero(Regex patron)
    {
        var raiz = RaizDelRepositorio();
        var directorio = Path.Combine(raiz, "src");
        Directory.Exists(directorio).Should().BeTrue("si src cambia de sitio, este ratchet deja de vigilar nada");

        var ficheros = new[] { "*.cs", "*.razor" }
            .SelectMany(ext => Directory.EnumerateFiles(directorio, ext, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        ficheros.Should().NotBeEmpty("src tiene que contener código que vigilar");

        return ficheros
            .Select(f => (Ruta: Path.GetRelativePath(raiz, f).Replace(Path.DirectorySeparatorChar, '/'),
                          Cuenta: File.ReadLines(f).Where(l => EsCodigoQueCasa(l, patron)).Sum(l => patron.Matches(l).Count)))
            .Where(x => x.Cuenta > 0)
            .ToDictionary(x => x.Ruta, x => x.Cuenta);
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
