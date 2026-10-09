using CaeManager.Application.Configuracion.Commands.GuardarFiltro;

namespace CaeManager.Application.Configuracion;

/// <summary>
/// Listados que recuerdan su última vista por Usuario y Tenant (decisión del
/// 2026-10-08). La cadena es la misma <c>Pantalla</c> que usan los filtros
/// guardados de ese listado (<see cref="PantallasConFiltrosGuardados"/>): una
/// pantalla tiene un solo nombre en la tabla, sirva la fila para un filtro con
/// nombre o para la vista recordada. Donde aún no hay filtros guardados, es el
/// nombre del componente de página.
/// </summary>
public static class PantallasConVistaRecordada
{
    public const string Trabajadores = PantallasConFiltrosGuardados.Trabajadores;
    public const string Empresas = "Empresas";
    public const string Clientes = PantallasConFiltrosGuardados.Clientes;
    public const string Documentos = PantallasConFiltrosGuardados.Documentos;
    public const string Centros = "Centros";
    public const string Subcontratas = "Subcontratas";
    public const string Vehiculos = "Vehiculos";
    public const string Proyectos = "Proyectos";
    public const string Visitas = "Visitas";
    public const string Gestiones = "Gestiones";

    public static readonly string[] Admitidas =
        [Trabajadores, Empresas, Clientes, Documentos, Centros, Subcontratas, Vehiculos, Proyectos, Visitas, Gestiones];

    /// <summary>
    /// Tope de <c>ValoresJson</c> en caracteres. La vista se escribe sola, sin que
    /// el usuario lo vea, así que la fila no puede crecer sin límite. Lo que guarda
    /// es el diccionario de parámetros de la URL del listado, y una URL no pasa de
    /// la línea de petición que admite el servidor (8 KB): el doble deja margen para
    /// las comillas y los escapes del JSON.
    /// </summary>
    public const int LongitudMaximaValoresJson = 16_384;
}
