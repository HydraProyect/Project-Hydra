using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.CatalogoBase;
using CaeManager.Domain.Trabajadores;

namespace CaeManager.Domain.Inbound.Requisitos;

/// <summary>
/// Un requisito de una <see cref="PlantillaRequisitos"/> del Tenant TCE (tarjeta 9; plan backend Inbound § 2.3): qué tipo
/// de documento propio lo acredita, a qué sujeto se pide, con qué origen, vigencia, tolerancia y bloqueo, a quién (los
/// modelos de contratación de <see cref="TiposContratacion"/>, el extranjero incluido desde el 2026-11-17) y qué
/// equivalencias acepta de un extranjero.
///
/// <para>
/// <b>Hijo del agregado.</b> Solo lo crean, editan y retiran los métodos de <see cref="PlantillaRequisitos"/>, que renuevan la
/// versión de la plantilla para que dos ediciones simultáneas se vean. No se borra: se retira con <see cref="Activo"/>, porque
/// los requerimientos y las evidencias de olas posteriores lo citan.
/// </para>
///
/// <para>
/// <b>Lo que no hace este incremento (B-14).</b> No resuelve capas ni crea requerimientos (B-15), no aplica el «solo nuevos o
/// también los N ya dados de alta» de la edición (revalidación, B-23). Quién es extranjero lo dice el modelo de contratación
/// del Trabajador (<see cref="TipoContratacion.Extranjero"/>), no este requisito.
/// </para>
/// </summary>
public sealed class RequisitoPlantilla : EntidadConTenant
{
    private const TiposContratacion TodosAnteriorAlExtranjero =
        TiposContratacion.CuentaAjena | TiposContratacion.Autonomo | TiposContratacion.Voluntario;

    private List<string> _equivalencias = [];

    public Guid PlantillaId { get; private set; }

    /// <summary>Tipo de documento del Tenant TCE que acredita el requisito. Fijo: cambiarlo es otro requisito.</summary>
    public Guid TipoDocumentoId { get; private set; }

    /// <summary>Requisito del catálogo base que adopta, o nulo si no adopta ninguno (siempre nulo en uno propio).</summary>
    public Guid? RequisitoBaseId { get; private set; }

    public EntidadRequisito Entidad { get; private set; }

    public OrigenRequisito Origen { get; private set; }

    public ModoVigenciaRequisito ModoVigencia { get; private set; }

    /// <summary>Meses de vigencia, solo con <see cref="ModoVigenciaRequisito.Fija"/>.</summary>
    public int? VigenciaMeses { get; private set; }

    /// <summary>
    /// Días que lo vencido sigue valiendo, tal como se fijaron. Para decidir, <see cref="ToleranciaEfectiva"/>: vuelve a
    /// aplicar R-6 sobre el tipo de ese momento.
    /// </summary>
    public int? ToleranciaDias { get; private set; }

    public bool BloqueaAcceso { get; private set; }

    public string? Criterio { get; private set; }

    /// <summary>
    /// Nacionalidad a la que se pedía el requisito antes del 2026-11-17. Ya no decide nada: a quién se pide lo dice solo
    /// <see cref="TiposContratacion"/>, y a un extranjero se le pide con <see cref="TiposContratacion.Extranjero"/>. La
    /// migración <c>TraduceExtranjeroAModeloDeContratacion</c> tradujo los valores guardados a la máscara.
    /// </summary>
    // shortcut: SePideA queda sin leerse; retirar la columna en un incremento posterior (decisión 2026-11-17)
    public DestinatarioRequisito SePideA { get; private set; }

    /// <summary>
    /// A qué modelos de contratación del Trabajador se pide: el único criterio, también para el extranjero
    /// (<see cref="TiposContratacion.Extranjero"/>). Fuera del ámbito Trabajador es siempre <see cref="TiposContratacion.Todos"/>.
    /// </summary>
    public TiposContratacion TiposContratacion { get; private set; }

    /// <summary>
    /// Equivalencias que acepta de un extranjero (texto libre del Gestor CAE). Los ejemplos de la maqueta están rotulados
    /// «pendiente de revisión legal» (DECISION_LOG 2026-11-14); aquí no hay ninguna de serie.
    /// </summary>
    public IReadOnlyList<string> Equivalencias => _equivalencias.AsReadOnly();

    public AmbitoDocumentoRequisito AmbitoDocumento { get; private set; }

    public bool Activo { get; private set; } = true;

    private RequisitoPlantilla()
    {
    }

    internal RequisitoPlantilla(PlantillaRequisitos plantilla, TipoDocumento tipo, RequisitoBase? requisitoBase, AtributosRequisito atributos)
    {
        PlantillaId = plantilla.Id;
        TipoDocumentoId = tipo.Id;
        Entidad = EntidadDe(tipo);
        Aplicar(plantilla, tipo, requisitoBase, atributos, adopta: true);
    }

    /// <summary>
    /// La tolerancia que cuenta hoy: la fijada, salvo que el tipo ya no la admita (R-6, <see cref="ReglaToleranciaRequisito"/>),
    /// en cuyo caso es 0. El tipo tiene que ser el de este requisito.
    /// </summary>
    public int ToleranciaEfectiva(TipoDocumento tipo)
    {
        ArgumentNullException.ThrowIfNull(tipo);
        if (tipo.Id != TipoDocumentoId)
            throw new ArgumentException("El tipo de documento no es el de este requisito.", nameof(tipo));
        return ToleranciaDias is { } dias && ReglaToleranciaRequisito.AdmiteTolerancia(tipo) ? dias : 0;
    }

    internal void Editar(PlantillaRequisitos plantilla, TipoDocumento tipo, RequisitoBase? requisitoBase, AtributosRequisito atributos)
    {
        if (!Activo)
            throw new InvalidOperationException("Un requisito retirado no se edita: se añade otro.");
        if (tipo.Id != TipoDocumentoId)
            throw new ArgumentException("El tipo de documento de un requisito no cambia: se añade otro requisito.", nameof(tipo));
        // Uno propio nació sin catálogo ni norma detrás: editarlo no lo convierte en obligación legal, de convenio ni de
        // Actividad («llamar ley a una práctica del sector»).
        if (Origen == OrigenRequisito.PropioTenant && atributos?.Origen != OrigenRequisito.PropioTenant)
            throw new ArgumentException("Un requisito propio del Tenant TCE conserva su origen.", nameof(atributos));
        // El requisito de base que adopta, como el tipo, no cambia al editar: omitirlo desadoptaría en silencio y dejaría
        // subir el origen sin la guarda de nivel. Adoptar otro, o ninguno, es otro requisito.
        if (requisitoBase?.Id != RequisitoBaseId)
            throw new ArgumentException("El requisito de base que adopta no cambia al editar: se añade otro requisito.", nameof(requisitoBase));
        Aplicar(plantilla, tipo, requisitoBase, atributos, adopta: false);
    }

    internal void Retirar() => Activo = false;

    internal static EntidadRequisito EntidadDe(TipoDocumento tipo) => tipo.AmbitoAplicacion switch
    {
        AmbitoAplicacion.Empresa => EntidadRequisito.Empresa,
        AmbitoAplicacion.Trabajador => EntidadRequisito.Trabajador,
        AmbitoAplicacion.Vehiculo => EntidadRequisito.VehiculoOMaquina,
        _ => throw new ArgumentException(
            "Un requisito de contrato de acceso se pide a la Empresa contratista, a un Trabajador o a un vehículo o máquina.",
            nameof(tipo)),
    };

    private void Aplicar(
        PlantillaRequisitos plantilla, TipoDocumento tipo, RequisitoBase? requisitoBase, AtributosRequisito atributos, bool adopta)
    {
        ArgumentNullException.ThrowIfNull(atributos);
        if (tipo.Archivado)
            throw new InvalidOperationException("Un tipo de documento archivado no se pide en una plantilla: se pide su sustituto.");

        var origen = ReglasCatalogoBase.ValidarEnum(atributos.Origen, nameof(atributos.Origen));
        ValidarOrigen(plantilla, origen, requisitoBase, adopta);

        var modo = ReglasCatalogoBase.ValidarEnum(atributos.ModoVigencia, nameof(atributos.ModoVigencia));
        if ((modo == ModoVigenciaRequisito.Fija) != atributos.VigenciaMeses.HasValue)
            throw new ArgumentException("La vigencia en meses se indica si y solo si la vigencia es fija.", nameof(atributos.VigenciaMeses));
        if (atributos.VigenciaMeses is < 1 or > AtributosRequisito.VigenciaMaximaMeses)
            throw new ArgumentOutOfRangeException(nameof(atributos.VigenciaMeses), atributos.VigenciaMeses,
                $"La vigencia va de 1 a {AtributosRequisito.VigenciaMaximaMeses} meses.");

        var tolerancia = atributos.ToleranciaDias is 0 ? null : atributos.ToleranciaDias;
        if (tolerancia is { } dias)
        {
            if (dias is < 0 or > AtributosRequisito.ToleranciaMaximaDias)
                throw new ArgumentOutOfRangeException(nameof(atributos.ToleranciaDias), dias,
                    $"La tolerancia va de 0 a {AtributosRequisito.ToleranciaMaximaDias} días.");
            if (modo == ModoVigenciaRequisito.SinVencimiento)
                throw new ArgumentException("Un requisito sin vencimiento no tiene tolerancia.", nameof(atributos.ToleranciaDias));
            if (!ReglaToleranciaRequisito.AdmiteTolerancia(tipo))
                throw new InvalidOperationException(
                    "La aptitud médica, la formación obligatoria y los seguros nunca tienen tolerancia (R-6), y un tipo sin grupo tampoco.");
        }

        var criterio = string.IsNullOrWhiteSpace(atributos.Criterio) ? null : atributos.Criterio.Trim();
        if (criterio?.Length > AtributosRequisito.LongitudMaximaCriterio)
            throw new ArgumentException($"El criterio tiene como máximo {AtributosRequisito.LongitudMaximaCriterio} caracteres.", nameof(atributos.Criterio));

        var entidad = EntidadDe(tipo);
        // shortcut: SePideA queda sin leerse; retirar la columna en un incremento posterior (decisión 2026-11-17)
        // En un requisito de Trabajador se sigue validando y guardando porque es parte del contrato de los Commands
        // (AtributosRequisito), pero no decide a quién se pide. En uno de Empresa contratista sí se sigue leyendo: la decisión
        // del Extranjero solo trata del Trabajador, y la del 2026-11-14 (el país de la Empresa) sigue vigente.
        var sePideA = ReglasCatalogoBase.ValidarEnum(atributos.SePideA, nameof(atributos.SePideA));
        if (entidad == EntidadRequisito.VehiculoOMaquina && sePideA != DestinatarioRequisito.Todos)
            throw new ArgumentException("Un vehículo o máquina no es nacional ni extranjero: se pide a todos.", nameof(atributos.SePideA));

        var contratacion = atributos.TiposContratacion & TiposContratacion.Todos;
        if (contratacion != atributos.TiposContratacion || contratacion == TiposContratacion.Ninguno)
            throw new ArgumentException("Marca al menos un tipo de contratación conocido.", nameof(atributos.TiposContratacion));
        // La migración TraduceExtranjeroAModeloDeContratacion no toca la máscara de Empresa ni de vehículo o máquina: su 7
        // (el Todos anterior al Extranjero) sigue valiendo como Todos, porque la máscara no acota esas entidades.
        if (entidad != EntidadRequisito.Trabajador && contratacion != TiposContratacion.Todos && contratacion != TodosAnteriorAlExtranjero)
            throw new ArgumentException("El tipo de contratación solo acota requisitos de Trabajador.", nameof(atributos.TiposContratacion));

        var equivalencias = NormalizarEquivalencias(atributos.Equivalencias);
        if (equivalencias.Count > 0 && sePideA == DestinatarioRequisito.Nacionales)
            throw new ArgumentException("Un requisito que solo se pide a nacionales no tiene equivalencias de extranjero.", nameof(atributos.Equivalencias));

        RequisitoBaseId = requisitoBase?.Id;
        Origen = origen;
        ModoVigencia = modo;
        VigenciaMeses = atributos.VigenciaMeses;
        ToleranciaDias = tolerancia;
        BloqueaAcceso = atributos.BloqueaAcceso;
        Criterio = criterio;
        SePideA = sePideA;
        TiposContratacion = contratacion;
        _equivalencias = equivalencias;
        AmbitoDocumento = ReglasCatalogoBase.ValidarEnum(atributos.AmbitoDocumento, nameof(atributos.AmbitoDocumento));
    }

    /// <summary>
    /// El origen dice con qué autoridad se pide, y está prohibido exagerarlo («llamar ley a una práctica del sector», ver
    /// <see cref="NaturalezaJuridica"/>): un requisito que adopta uno de base no sube su nivel, uno propio no adopta ninguno
    /// y una exigencia del Cliente empresarial es siempre N3 (D-15).
    /// </summary>
    /// <param name="adopta">
    /// True al añadir el requisito. Solo entonces se exige que el de base siga activo: si Plataforma lo retira después, el
    /// Tenant TCE sigue pudiendo editar el criterio o la vigencia del que ya adoptó, en vez de quedar bloqueado.
    /// </param>
    private static void ValidarOrigen(PlantillaRequisitos plantilla, OrigenRequisito origen, RequisitoBase? requisitoBase, bool adopta)
    {
        if (plantilla.Tipo == TipoPlantillaRequisitos.ExigenciasClienteEmpresarial && origen != OrigenRequisito.ExigenciaN3)
            throw new ArgumentException("En la plantilla de exigencias del Cliente empresarial todo requisito es una exigencia N3.", nameof(origen));

        // Sin base del catálogo revisado por el técnico habilitado (CA-3, CA-7) no hay norma, convenio ni práctica de
        // Actividad que respalde el requisito: es propio del Tenant TCE o exigencia del Cliente empresarial (CA-6;
        // DECISION_LOG 2026-11-15, D2 de B-14). Como se aplica al añadir y al editar, retirar un requisito propio y
        // volver a añadir su tipo no lo convierte en legal.
        if (requisitoBase is null)
        {
            if (origen is not (OrigenRequisito.PropioTenant or OrigenRequisito.ExigenciaN3))
                throw new ArgumentException(
                    "Un requisito legal, de convenio o de Actividad adopta uno del catálogo base: sin él es propio del Tenant TCE " +
                    "o exigencia del Cliente empresarial.", nameof(origen));
            return;
        }

        if (adopta && !requisitoBase.Activo)
            throw new InvalidOperationException("Un requisito retirado del catálogo base no se adopta.");
        if (origen == OrigenRequisito.PropioTenant)
            throw new ArgumentException("Un requisito propio del Tenant TCE no adopta uno del catálogo base.", nameof(origen));

        var exagera = (requisitoBase.Nivel, origen) switch
        {
            (NivelExigencia.N3, OrigenRequisito.LegalN1 or OrigenRequisito.Convenio) => true,
            (NivelExigencia.N2, OrigenRequisito.LegalN1) => true,
            _ => false,
        };
        if (exagera)
            throw new ArgumentException(
                $"El requisito de base es {requisitoBase.Nivel}: no se presenta con un origen de nivel superior.", nameof(origen));
    }

    private static List<string> NormalizarEquivalencias(IReadOnlyList<string>? equivalencias)
    {
        var lista = (equivalencias ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (lista.Count > AtributosRequisito.MaximoEquivalencias)
            throw new ArgumentException($"Como mucho {AtributosRequisito.MaximoEquivalencias} equivalencias.", nameof(equivalencias));
        if (lista.Any(e => e.Length > AtributosRequisito.LongitudMaximaEquivalencia))
            throw new ArgumentException(
                $"Cada equivalencia tiene como máximo {AtributosRequisito.LongitudMaximaEquivalencia} caracteres.", nameof(equivalencias));
        return lista;
    }
}
