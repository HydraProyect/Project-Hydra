using CaeManager.Domain.Common;

namespace CaeManager.Domain.CatalogoBase;

/// <summary>
/// Un riesgo del catálogo cerrado de Plataforma (CA-4): la IA propone, de entre estos, los riesgos que menciona la
/// evaluación de riesgos de la Empresa contratista, y una persona del Tenant TCE los valida (art. 24.2 LPRL).
///
/// <para>
/// El catálogo es cerrado pero lleva una opción «otro» (<see cref="EsOtro"/>, R4 del técnico PRL), para el riesgo
/// evaluado que no encaja en ninguno. Como mucho hay una: lo impone un índice único parcial. Lo que el Tenant TCE
/// escriba sobre ese riesgo es dato suyo, del contrato, y no vive aquí.
/// </para>
///
/// <para>
/// <b>Catálogo global, sin Tenant</b>, como <see cref="ActividadCatalogo"/>: el runtime solo lo lee, y su contenido
/// solo se carga tras la revisión de un técnico habilitado (CA-7). Se retira con <see cref="Activo"/>, no se borra.
/// </para>
/// </summary>
public sealed class RiesgoCatalogo : Entity
{
    public string Codigo { get; private set; } = string.Empty;

    public string Nombre { get; private set; } = string.Empty;

    /// <summary>La opción «otro» del catálogo cerrado: el riesgo evaluado que no encaja en ninguno de los demás.</summary>
    public bool EsOtro { get; private set; }

    public bool Activo { get; private set; } = true;

    private RiesgoCatalogo()
    {
    }

    private RiesgoCatalogo(string codigo, string nombre, bool esOtro)
    {
        Codigo = ReglasCatalogoBase.ValidarCodigo(codigo, nameof(codigo));
        Nombre = ReglasCatalogoBase.ValidarTexto(nombre, ReglasCatalogoBase.LongitudMaximaNombre, nameof(nombre));
        EsOtro = esOtro;
    }

    public static RiesgoCatalogo Nuevo(string codigo, string nombre) => new(codigo, nombre, esOtro: false);

    /// <summary>La opción «otro» del catálogo cerrado.</summary>
    public static RiesgoCatalogo Otro(string codigo, string nombre) => new(codigo, nombre, esOtro: true);

    public void Retirar() => Activo = false;

    public void Reactivar() => Activo = true;
}
