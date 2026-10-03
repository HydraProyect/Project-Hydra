namespace CaeManager.Domain.Documentos;

/// <summary>
/// De quién se mide un porcentaje de cumplimiento documental. Decisión del propietario, 2026-10-03:
/// cada porcentaje mide SU contexto y no existe una cifra de «archivo entero» del Tenant.
/// <list type="bullet">
/// <item><see cref="Centro"/> — la documentación exigida por el Centro a los Trabajadores con Asignación activa en él.</item>
/// <item><see cref="Trabajador"/> — la documentación exigida al Trabajador en todos los Centros donde tiene Asignación activa.</item>
/// <item><see cref="Empresa"/> — la documentación de los Trabajadores de esa Empresa (la que le exigen los Centros donde trabajan).
/// Los documentos de ámbito Empresa no entran todavía: ver «Fuera del universo» en <see cref="CumplimientoDocumental"/>.</item>
/// <item><see cref="ClienteEmpresarial"/> — toda la documentación exigida en los Centros de ese Cliente empresarial.</item>
/// </list>
/// </summary>
public enum ContextoCumplimiento
{
    Centro = 0,
    Trabajador = 1,
    Empresa = 2,
    ClienteEmpresarial = 3
}

/// <summary>
/// Un par Trabajador × TipoDocumento que un Centro exige hoy, con el estado del documento que lo representa
/// (<see cref="EstadoDocumento.Faltante"/> si no hay ninguno). Es la unidad con la que se mide cualquier contexto de
/// <see cref="ContextoCumplimiento"/>: el mismo par cuenta una vez por Centro que lo exige.
/// </summary>
/// <param name="CentroId">Centro que exige el tipo.</param>
/// <param name="ClienteEmpresarialId"><c>Centro.ClienteId</c>: la Empresa en posición de Cliente empresarial de ese Centro.</param>
/// <param name="EmpresaId">Empresa del Trabajador, o <c>null</c> si no tiene.</param>
/// <param name="TrabajadorId">Trabajador al que se le exige.</param>
/// <param name="TipoDocumentoId">Tipo exigido.</param>
/// <param name="Estado">Estado del documento preferido del par (<c>PreferenciaDocumentoPorTipo</c>), o Faltante.</param>
public readonly record struct ParDocumentalExigido(
    Guid CentroId,
    Guid ClienteEmpresarialId,
    Guid? EmpresaId,
    Guid TrabajadorId,
    Guid TipoDocumentoId,
    EstadoDocumento Estado);

/// <summary>
/// Fracción de cumplimiento: cuántos de los requeridos están al día. <see cref="Porcentaje"/> es <c>null</c> cuando no
/// hay ningún requerido — un 0 % o un 100 % ahí sería engañoso, «sin requisitos» es la lectura correcta.
/// </summary>
public sealed record FraccionCumplimiento(int AlDia, int Requeridos)
{
    public static readonly FraccionCumplimiento SinRequisitos = new(0, 0);

    public int? Porcentaje => Requeridos == 0 ? null : (int)Math.Round(AlDia * 100.0 / Requeridos);

    public static FraccionCumplimiento Sumar(IEnumerable<FraccionCumplimiento> fracciones) =>
        new(fracciones.Sum(f => f.AlDia), fracciones.Sum(f => f.Requeridos));
}

/// <summary>
/// Punto único de la definición de cumplimiento documental: qué cuenta como «al día» (numerador), qué entra en el
/// denominador y de quién se mide cada porcentaje (<see cref="ContextoCumplimiento"/>). Toda superficie que enseña un
/// porcentaje, o una fracción «n/m», lo obtiene de aquí; una copia de estas reglas en otro sitio es el defecto que
/// midió S4 (cinco fórmulas con cifras distintas para los mismos datos) y la vigila
/// <c>ReglasDeNegocioSinCopiasTests</c>.
///
/// <para>
/// <b>Numerador.</b> Un par está al día si el estado de su documento es <see cref="EstadoDocumento.Vigente"/>,
/// <see cref="EstadoDocumento.Proximo"/>, <see cref="EstadoDocumento.Urgente"/> (siguen siendo válidos hoy) o
/// <see cref="EstadoDocumento.SinCaducidad"/> (confirmado que no caduca). Están fuera:
/// <see cref="EstadoDocumento.Vencido"/>, <see cref="EstadoDocumento.Faltante"/> y
/// <see cref="EstadoDocumento.SinConfirmar"/>. Esto último NO es la regla de los paneles ni de las incidencias, donde
/// «Sin confirmar» cuenta como al día con aviso (decisión 2026-10-01): en un porcentaje es no conforme (decisión
/// 2026-10-03), porque nadie ha comprobado que el documento valga.
/// </para>
///
/// <para>
/// <b>Denominador.</b> Todo par exigido, al día o no, con documento o sin él. «Sin caducidad» entra en los dos lados.
/// </para>
///
/// <para>
/// <b>Fuera del universo, a propósito, hasta que el propietario decida.</b> (1) Los documentos de ámbito Empresa: hoy
/// ningún porcentaje los cuenta y el cálculo no sabe qué tipos de Empresa exige cada Centro. (2) El tratamiento de los
/// documentos históricos sustituidos: se aplica la preferencia vigente de <c>PreferenciaDocumentoPorTipo</c> (uno por
/// par) y no se decide otra.
/// </para>
/// </summary>
public static class CumplimientoDocumental
{
    /// <summary>El numerador: ¿cuenta este estado como «al día» en un porcentaje?</summary>
    public static bool EsConforme(EstadoDocumento estado) => estado is
        EstadoDocumento.SinCaducidad or EstadoDocumento.Vigente or EstadoDocumento.Proximo or EstadoDocumento.Urgente;

    /// <summary>Fracción de un conjunto de pares requeridos: todos entran en el denominador, solo los conformes en el numerador.</summary>
    public static FraccionCumplimiento Evaluar(IEnumerable<EstadoDocumento> estados)
    {
        var alDia = 0;
        var requeridos = 0;
        foreach (var estado in estados)
        {
            requeridos++;
            if (EsConforme(estado)) alDia++;
        }

        return new FraccionCumplimiento(alDia, requeridos);
    }

    /// <summary>Lo mismo, desde recuentos por estado (los KPI agregan en SQL una fila por estado, no una por documento).</summary>
    public static FraccionCumplimiento Evaluar(IEnumerable<(EstadoDocumento Estado, int Cantidad)> conteos)
    {
        var alDia = 0;
        var requeridos = 0;
        foreach (var (estado, cantidad) in conteos)
        {
            requeridos += cantidad;
            if (EsConforme(estado)) alDia += cantidad;
        }

        return new FraccionCumplimiento(alDia, requeridos);
    }

    /// <summary>Fracción de un contexto: los pares exigidos que le pertenecen, medidos con <see cref="Evaluar(IEnumerable{EstadoDocumento})"/>.</summary>
    public static FraccionCumplimiento De(ContextoCumplimiento contexto, Guid id, IEnumerable<ParDocumentalExigido> pares) =>
        Evaluar(pares.Where(p => Pertenece(contexto, id, p)).Select(p => p.Estado));

    /// <summary>La fracción de cada contexto presente en los pares. Un contexto sin ningún par exigido no aparece.</summary>
    public static Dictionary<Guid, FraccionCumplimiento> PorContexto(ContextoCumplimiento contexto, IEnumerable<ParDocumentalExigido> pares) =>
        pares
            .Where(p => Clave(contexto, p) is not null)
            .GroupBy(p => Clave(contexto, p)!.Value)
            .ToDictionary(g => g.Key, g => Evaluar(g.Select(p => p.Estado)));

    private static bool Pertenece(ContextoCumplimiento contexto, Guid id, ParDocumentalExigido par) => Clave(contexto, par) == id;

    private static Guid? Clave(ContextoCumplimiento contexto, ParDocumentalExigido par) => contexto switch
    {
        ContextoCumplimiento.Centro => par.CentroId,
        ContextoCumplimiento.Trabajador => par.TrabajadorId,
        ContextoCumplimiento.Empresa => par.EmpresaId,
        ContextoCumplimiento.ClienteEmpresarial => par.ClienteEmpresarialId,
        _ => throw new ArgumentOutOfRangeException(nameof(contexto), contexto, "Contexto de cumplimiento desconocido.")
    };
}
