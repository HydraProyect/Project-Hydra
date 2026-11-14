using CaeManager.Domain.Common;

namespace CaeManager.Domain.CatalogoBase;

/// <summary>
/// Un requisito del catálogo base de Plataforma, con la norma que lo funda y su nivel de exigencia
/// (CONTRATO-ACCESO-ACTIVIDAD-Y-REQUISITOS § 4.1, capa 1). No es un <c>TipoDocumento</c>: esos son del Tenant, y el
/// Tenant TCE liga cada requisito de base a los suyos en su plantilla.
///
/// <para>
/// <b>Catálogo global, sin Tenant</b>, como <see cref="RiesgoCatalogo"/>: el runtime solo lo lee y su contenido espera
/// a la revisión de un técnico habilitado (CA-7). Se retira con <see cref="Activo"/>, no se borra.
/// </para>
/// </summary>
public sealed class RequisitoBase : Entity
{
    public string Codigo { get; private set; } = string.Empty;

    public string Nombre { get; private set; } = string.Empty;

    /// <summary>Norma, artículo y apartado que lo exige, o el convenio o la práctica que lo propone (N3).</summary>
    public string Fundamento { get; private set; } = string.Empty;

    public NivelExigencia Nivel { get; private set; }

    public bool Activo { get; private set; } = true;

    private RequisitoBase()
    {
    }

    public RequisitoBase(string codigo, string nombre, string fundamento, NivelExigencia nivel)
    {
        Codigo = ReglasCatalogoBase.ValidarCodigo(codigo, nameof(codigo));
        Nombre = ReglasCatalogoBase.ValidarTexto(nombre, ReglasCatalogoBase.LongitudMaximaNombre, nameof(nombre));
        Fundamento = ReglasCatalogoBase.ValidarTexto(fundamento, ReglasCatalogoBase.LongitudMaximaFundamento, nameof(fundamento));
        Nivel = ReglasCatalogoBase.ValidarEnum(nivel, nameof(nivel));
    }

    public void Retirar() => Activo = false;

    public void Reactivar() => Activo = true;
}

/// <summary>
/// Regla de base «riesgo → requisito»: un riesgo validado en el contrato propone este requisito de base. No hay una
/// relación unívoca entre riesgo y requisito, así que es de muchos a muchos: un riesgo propone varios requisitos y un
/// requisito lo proponen varios riesgos, pero cada par una sola vez. El Tenant TCE decide después en su plantilla qué
/// adopta (CA-1, capa 2); la regla de base sola no exige nada.
/// </summary>
public sealed class ReglaRiesgoRequisito : Entity
{
    public Guid RiesgoId { get; private set; }

    public Guid RequisitoId { get; private set; }

    private ReglaRiesgoRequisito()
    {
    }

    public ReglaRiesgoRequisito(RiesgoCatalogo riesgo, RequisitoBase requisito)
    {
        ArgumentNullException.ThrowIfNull(riesgo);
        ArgumentNullException.ThrowIfNull(requisito);
        RiesgoId = riesgo.Id;
        RequisitoId = requisito.Id;
    }
}
