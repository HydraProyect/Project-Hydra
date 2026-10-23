namespace CaeManager.Domain.Documentos;

/// <summary>
/// Orden de gravedad de un <see cref="EstadoDocumento"/> cuando hay que quedarse con el peor de varios o
/// listar primero lo que más urge. Es el ÚNICO sitio donde vive ese orden: lo vencido, antes que lo que falta
/// (orden fijado el 2026-10-03: Vencido antes que Faltante en todas las superficies; hasta el 2026-10-08 este
/// rango los tenía al revés), antes que lo vencido pero en tolerancia (<see cref="EstadoDocumento.EnTolerancia"/>: sigue valiendo para acceder, pero ya
/// venció), antes que lo urgente, lo próximo, lo que no se sabe (<see cref="EstadoDocumento.SinConfirmar"/>, detrás de lo
/// malo conocido y delante de lo vigente), lo vigente y, al final, lo que no caduca.
///
/// <para>
/// Hasta S4 (2026-10-02) seis pantallas y consultas repetían este diccionario a mano. Un orden repetido es una
/// regla que puede divergir: lo comprueba <c>CoherenciaDeLaSeveridadDelEstadoTests</c> y lo vigila el ratchet
/// <c>ReglasDeNegocioSinCopiasTests</c>.
/// </para>
///
/// <para>
/// No es el orden de <c>EstadoDocumentalFiltro.ClaveOrden</c> (ordena PROPIETARIOS por su peor estado, donde
/// <see cref="EstadoDocumento.Faltante"/> no existe y «sin documentos» va al final) ni el orden de los bloques de
/// la pantalla Alertas (Vencido antes que Faltante, por mockup): son criterios de otra pregunta, declarados como
/// excepción en el ratchet.
/// </para>
/// </summary>
public static class SeveridadEstadoDocumento
{
    /// <summary>Rango de gravedad: 0 es lo más grave. Ordena ascendente para tener lo peor primero.</summary>
    public static int Rango(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido => 0,
        EstadoDocumento.Faltante => 1,
        EstadoDocumento.EnTolerancia => 2,
        EstadoDocumento.Urgente => 3,
        EstadoDocumento.Proximo => 4,
        EstadoDocumento.SinConfirmar => 5,
        EstadoDocumento.Vigente => 6,
        EstadoDocumento.SinCaducidad => 7,
        _ => 8
    };

    /// <summary>
    /// El peor estado de un propietario con Tipos exigidos sin documento: Vencido, luego Pendiente
    /// (<see cref="EstadoDocumento.Faltante"/>), luego lo demás, por <see cref="Rango"/>. Lo usa la búsqueda global, que trae
    /// el peor estado y los faltantes por separado. Los listados de Empresas y de Trabajadores, Empresa 360 y la cabecera
    /// de Trabajador 360 no lo usan: combinan con <see cref="ConRequisitosObligatorios"/>.
    /// </summary>
    public static EstadoDocumento? ConFaltantes(EstadoDocumento? estado, bool hayFaltantes) =>
        hayFaltantes && (estado is null || Rango(EstadoDocumento.Faltante) < Rango(estado.Value))
            ? EstadoDocumento.Faltante
            : estado;

    /// <summary>
    /// El estado de una Empresa o de un Trabajador con sus requisitos OBLIGATORIOS de Centro encima (un Tipo de su ámbito que
    /// un Centro donde opera marca «bloquea el acceso»; <c>RequisitosObligatoriosIncumplidos</c>, decisiones del 2026-10-23).
    /// En rojo solo lo que bloquea el acceso: un obligatorio vencido allí es <see cref="EstadoDocumento.Vencido"/>, y uno que
    /// falta, <see cref="EstadoDocumento.Faltante"/> («Pendiente»). Sin obligatorios incumplidos manda el peor estado de
    /// vigencia de sus documentos, y si ese es un vencido de algo que ningún Centro marca obligatorio, es un aviso
    /// (<see cref="EsVencidoSinBloqueo"/>), no rojo. El estado de vigencia del Documento no cambia: sigue Vencido.
    /// Es la única combinación: el listado de Empresas lee el resultado ya combinado de <c>ObtenerEmpresasQuery</c>, y el
    /// listado de Trabajadores, Empresa 360 y la cabecera de Trabajador 360, el de <c>ObtenerTrabajadoresQuery</c>.
    /// </summary>
    /// <param name="peorVigencia">Peor estado de vigencia de los documentos del propietario; <c>null</c> si no tiene ninguno.</param>
    /// <param name="obligatorioVencido">Algún requisito obligatorio de un Centro visible tiene documento, pero ya no vale allí.</param>
    /// <param name="obligatorioAusente">Algún requisito obligatorio de un Centro visible no tiene ningún documento.</param>
    public static EstadoDocumento? ConRequisitosObligatorios(
        EstadoDocumento? peorVigencia, bool obligatorioVencido, bool obligatorioAusente) =>
        obligatorioVencido ? EstadoDocumento.Vencido
        : obligatorioAusente ? EstadoDocumento.Faltante
        : peorVigencia;

    /// <summary>
    /// ¿El <see cref="EstadoDocumento.Vencido"/> que resulta de <see cref="ConRequisitosObligatorios"/> es solo un aviso? Lo es
    /// cuando viene de un documento vencido que ningún Centro visible marca obligatorio: se pinta en el tono de aviso, no en
    /// rojo (decisión del 2026-10-23).
    /// </summary>
    public static bool EsVencidoSinBloqueo(EstadoDocumento? peorVigencia, bool obligatorioVencido, bool obligatorioAusente) =>
        peorVigencia == EstadoDocumento.Vencido && !obligatorioVencido && !obligatorioAusente;
}
