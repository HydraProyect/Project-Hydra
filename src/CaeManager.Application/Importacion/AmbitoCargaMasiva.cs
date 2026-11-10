using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Importacion;

/// <summary>
/// Ámbito efectivo del acto que ejecuta una carga masiva, aplicado fila a fila. Cartera ≠ aislamiento de Tenant ≠
/// privilegio de plataforma: esto solo recorta lo que el Coordinador CAE (o la Dirección CAE, si su alcance no es
/// total) puede escribir dentro del Tenant actual; el aislamiento lo sigue garantizando RLS.
///
/// <para>
/// <see cref="AutoridadCargaMasiva.Total"/> (acto de Administrador) no consulta el alcance: no recorta nada. En la
/// autoridad acotada se usan las mismas listas que los comandos unitarios (de gestión para Empresas y Centros, de visibilidad para Clientes y Trabajadores, que no tienen variante de gestión: CrearTrabajador, CrearCentro,
/// CrearAsignacion…), de modo que una carga masiva nunca escribe lo que el comando unitario del mismo usuario
/// rechazaría; <c>null</c> en una lista significa «sin restricción», como en <see cref="IAlcanceDatosService"/>.
/// </para>
///
/// <para>
/// Lo que el propio fichero crea NO queda dentro del ámbito por haberlo creado (D-7: crear un Cliente empresarial no
/// concede Asignación de Cartera), salvo los Trabajadores, que solo se crean si su Empresa ya estaba dentro. Una Empresa
/// que crea la propia carga tampoco entra (decisión de Chris, 2026-11-03): su fila y las que la citan llevan un texto que
/// dice qué hacer (<see cref="AvisoEmpresaCreadaFueraDeCartera"/>, <see cref="MotivoEmpresaCreadaFueraDeCartera"/>).
/// </para>
/// </summary>
public sealed class AmbitoCargaMasiva
{
    private readonly bool _sinRestriccion;
    private readonly HashSet<Guid>? _clientes;
    private readonly HashSet<Guid>? _empresas;
    private readonly HashSet<Guid>? _centros;
    private readonly HashSet<Guid>? _trabajadores;
    private readonly HashSet<Guid> _trabajadoresCreados = [];
    private readonly HashSet<Guid> _empresasCreadas = [];

    /// <summary>
    /// La Asignación de Cartera universal alcanza el Tenant entero aunque sus listas sean explícitas
    /// (<see cref="IAlcanceDatosService.AlcanzaElTenantEnteroAsync"/>). Solo decide los MOTIVOS: no hay nada fuera del
    /// ámbito que ocultar, así que se conservan los accionables. Las comprobaciones de escritura siguen usando las listas.
    /// </summary>
    private bool _alcanzaElTenantEntero;

    private AmbitoCargaMasiva(
        bool sinRestriccion, IReadOnlyList<Guid>? clientes, IReadOnlyList<Guid>? empresas,
        IReadOnlyList<Guid>? centros, IReadOnlyList<Guid>? trabajadores)
    {
        _sinRestriccion = sinRestriccion;
        _clientes = clientes is null ? null : [.. clientes];
        _empresas = empresas is null ? null : [.. empresas];
        _centros = centros is null ? null : [.. centros];
        _trabajadores = trabajadores is null ? null : [.. trabajadores];
    }

    public static async Task<AmbitoCargaMasiva> ResolverAsync(
        AutoridadCargaMasiva autoridad, IAlcanceDatosService alcance, CancellationToken cancellationToken)
    {
        if (autoridad == AutoridadCargaMasiva.Total)
            return new AmbitoCargaMasiva(true, null, null, null, null);

        // Fallo cerrado: sin autoridad no hay ámbito (el handler ya habrá devuelto el error antes).
        if (autoridad != AutoridadCargaMasiva.Acotada)
            return new AmbitoCargaMasiva(false, [], [], [], []);

        return new AmbitoCargaMasiva(
            false,
            await alcance.ObtenerClienteIdsVisiblesAsync(cancellationToken),
            await alcance.ObtenerEmpresaIdsParaGestionAsync(cancellationToken),
            await alcance.ObtenerCentroIdsParaGestionAsync(cancellationToken),
            await alcance.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken))
        {
            _alcanzaElTenantEntero = await alcance.AlcanzaElTenantEnteroAsync(cancellationToken),
        };
    }

    public bool Cliente(Guid id) => _sinRestriccion || _clientes is null || _clientes.Contains(id);

    /// <summary>
    /// Si la confirmación puede nombrar a quien carga una Empresa en posición de Cliente empresarial que no está en ninguna
    /// lista, como una dada de baja (el filtro global la saca también de las listas de la cartera universal). Cierto si el
    /// Cliente empresarial está en el ámbito (su id figura en la lista de quien carga: nombrarlo no revela nada de fuera)
    /// o si la lista de Clientes no recorta (autoridad Total, lista sin restricción o
    /// Asignación de Cartera universal expresada como lista): entonces no hay nada fuera del ámbito que ocultar. No concede
    /// escritura: solo decide si se da el error accionable o el motivo uniforme.
    /// </summary>
    public bool PuedeNombrarCliente(Guid id) => !Recorta(EntidadDelAmbito.Cliente) || Cliente(id);

    public bool Empresa(Guid id) => _sinRestriccion || _empresas is null || _empresas.Contains(id);

    public bool Centro(Guid id) => _sinRestriccion || _centros is null || _centros.Contains(id);

    public bool Trabajador(Guid id) =>
        _sinRestriccion || _trabajadores is null || _trabajadores.Contains(id) || _trabajadoresCreados.Contains(id);

    /// <summary>Un Trabajador creado por esta carga (solo se crea con su Empresa dentro del ámbito) está dentro de él.</summary>
    public void RegistrarTrabajadorCreado(Guid id) => _trabajadoresCreados.Add(id);

    /// <summary>
    /// Recuerda una Empresa creada por esta carga SIN meterla en el ámbito (decisión de Chris, 2026-11-03: ninguna
    /// importación amplía por sí sola la Asignación de Cartera de nadie). Solo sirve para que el resultado diga a quien
    /// carga qué hacer con ella y con las filas que la citan, en vez del motivo uniforme de «fuera de tu ámbito».
    /// </summary>
    public void RegistrarEmpresaCreada(Guid id) => _empresasCreadas.Add(id);

    /// <summary>La Empresa la creó esta carga y, aun así, queda fuera del ámbito de quien carga.</summary>
    public bool EsEmpresaCreadaFueraDelAmbito(Guid id) => _empresasCreadas.Contains(id) && !Empresa(id);

    /// <summary>
    /// Aviso de la fila de una Empresa recién creada que queda fuera del ámbito de quien carga durante esta carga;
    /// <c>null</c> si queda dentro (autoridad Total o lista de Empresas sin restricción). No revela nada de fuera del
    /// ámbito: la Empresa la acaba de crear quien carga.
    /// <para>
    /// No afirma que haga falta pedir nada: tras la carga, la Empresa puede entrar sola en las Empresas visibles (la cartera
    /// universal, o la Empresa propia del Tenant que ve un Coordinador CAE con cartera no vacía, D-8). Por eso manda volver a
    /// cargar y solo pedir a la Dirección CAE que amplíe la Asignación de Cartera si sigue fuera.
    /// </para>
    /// </summary>
    public string? AvisoEmpresaCreadaFueraDeCartera(Guid id, string razonSocial) =>
        EsEmpresaCreadaFueraDelAmbito(id)
            ? $"Empresa «{razonSocial}» creada, pero no queda en tu ámbito durante esta carga: no se da de alta nada que dependa de ella en este archivo. Si al volver a cargar esas filas sigue fuera de tu ámbito, pide a la Dirección CAE que amplíe tu Asignación de Cartera para verla."
            : null;

    /// <summary>
    /// Motivo de una fila que no se escribe porque cita una Empresa que esta misma carga acaba de crear y que queda fuera
    /// del ámbito (ver <see cref="RegistrarEmpresaCreada"/> y <see cref="AvisoEmpresaCreadaFueraDeCartera"/>). Sustituye al
    /// motivo uniforme: la Empresa no es «algo de fuera» que ocultar, la creó quien carga.
    /// </summary>
    public static string MotivoEmpresaCreadaFueraDeCartera(string razonSocial) =>
        $"La empresa «{razonSocial}» se ha creado con esta importación, pero no queda en tu ámbito durante la misma carga: la fila no se ha escrito. Vuelve a cargar esta fila; si sigue fuera de tu ámbito, pide a la Dirección CAE que amplíe tu Asignación de Cartera para verla.";

    /// <summary>Motivo uniforme con el que se omite y se cuenta una fila fuera del ámbito; nunca nombra qué hay fuera.</summary>
    public static string Motivo(string quePide) =>
        $"{quePide} queda fuera de tu ámbito (tu Asignación de Cartera): la fila no se ha escrito. Pídele la carga a quien tenga ese ámbito.";

    /// <summary>
    /// Motivo de una fila cuyo DNI está en la Papelera. La Papelera no tiene ámbito decidible (el eliminado no está en
    /// ninguna lista visible), así que con una lista de Trabajadores que recorta se da el mismo motivo que fuera de ámbito:
    /// «restáuralo» revelaría a un Coordinador CAE que ese DNI existe en el Tenant aunque no sea de su cartera. Con la
    /// Asignación de Cartera universal, aunque se exprese como lista, no recorta y se conserva el accionable (ver
    /// <see cref="MotivoPapeleraSinDistinguir"/>).
    /// </summary>
    public string MotivoPapelera(string dni) => MotivoPapeleraSinDistinguir("Un trabajador con ese DNI", dni);

    /// <summary>
    /// Motivo de un documento cuyo trabajador no existe ni pudo crearse. Con ámbito acotado es el mismo que el de un
    /// trabajador fuera de ámbito: «no existe» frente a «fuera de tu ámbito» revelaría a un Coordinador CAE que ese DNI
    /// existe en el Tenant sin intentar crear nada (no es el oráculo de clave única aceptado).
    /// </summary>
    public string MotivoTitularDeDocumentoInexistente(string motivoSinRestriccion) =>
        MotivoSinDistinguirExistencia("El trabajador de este documento", EntidadDelAmbito.Trabajador, motivoSinRestriccion);

    /// <summary>
    /// Motivo de una fila que no se escribe porque algo de lo que depende (el Trabajador o el Centro de una Asignación, la
    /// Empresa de un Trabajador, el Cliente empresarial o el Centro de la hoja de Centros) no existe o queda fuera del
    /// ámbito. Si la lista de <paramref name="citada"/> recorta (autoridad acotada con lista explícita que no sea la de una
    /// Asignación de Cartera universal), las dos causas dan
    /// el MISMO motivo uniforme, con el mismo <paramref name="quePide"/> que la rama «fuera de ámbito» de esa fila: la fila
    /// no intenta crear lo que cita, así que distinguirlas sería un oráculo de existencia que no es el de clave única
    /// aceptado. Si no recorta (autoridad Total, o acotada que alcanza el Tenant entero para esa entidad), no hay nada fuera
    /// del ámbito que ocultar y se conserva el motivo accionable.
    /// </summary>
    public string MotivoSinDistinguirExistencia(string quePide, EntidadDelAmbito citada, string motivoSinRestriccion) =>
        Recorta(citada) ? Motivo(quePide) : motivoSinRestriccion;

    /// <summary>
    /// Motivo de una fila cuyo DNI está en la Papelera, con el <paramref name="quePide"/> de la hoja. Si la lista de
    /// Trabajadores recorta, el mismo motivo uniforme que «no existe» y «fuera de ámbito» en esa fila (nunca «restáuralo»,
    /// que revelaría que el DNI existe en el Tenant); si no recorta, quien carga alcanza el Tenant entero (también los
    /// agregados de filas eliminadas) y conserva el accionable, igual que con autoridad Total.
    /// </summary>
    public string MotivoPapeleraSinDistinguir(string quePide, string dni) =>
        MotivoSinDistinguirExistencia(quePide, EntidadDelAmbito.Trabajador, DnisEnPapelera.Motivo(dni));

    private bool Recorta(EntidadDelAmbito entidad) => !_sinRestriccion && !_alcanzaElTenantEntero && entidad switch
    {
        EntidadDelAmbito.Cliente => _clientes is not null,
        EntidadDelAmbito.Empresa => _empresas is not null,
        EntidadDelAmbito.Centro => _centros is not null,
        EntidadDelAmbito.Trabajador => _trabajadores is not null,
        _ => true,
    };
}

/// <summary>Qué lista del ámbito decide si una fila puede tener algo fuera de él (ver <see cref="AmbitoCargaMasiva.MotivoSinDistinguirExistencia"/>).</summary>
public enum EntidadDelAmbito
{
    Cliente,
    Empresa,
    Centro,
    Trabajador,
}

/// <summary>Errores de autoridad compartidos por los comandos de carga masiva del Importador.</summary>
public static class ErroresCargaMasiva
{
    public static readonly Error SinAutoridad = Error.Crear(
        "Importacion.SinAutoridadCargaMasiva",
        "Solo el Administrador, la Dirección CAE o el Coordinador CAE pueden ejecutar una carga masiva.");
}
