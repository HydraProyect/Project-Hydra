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
    /// El estado documental de un propietario al que un Centro le exige un Tipo de documento que no tiene. El peor
    /// estado de sus documentos registrados no ve lo que no existe: sin esto, quien no ha subido nada de lo exigido se
    /// leería como «Sin incidencias». Si ya hay algo más grave (un vencido), manda ese: Vencido, luego Pendiente
    /// (<see cref="EstadoDocumento.Faltante"/>), luego lo que está por vencer y lo demás, por <see cref="Rango"/>.
    /// Es la única combinación de los dos: el listado de Trabajadores, Empresa 360 y la cabecera de Trabajador 360
    /// leen el resultado ya combinado de <c>ObtenerTrabajadoresQuery</c>.
    /// </summary>
    public static EstadoDocumento? ConFaltantes(EstadoDocumento? estado, bool hayFaltantes) =>
        hayFaltantes && (estado is null || Rango(EstadoDocumento.Faltante) < Rango(estado.Value))
            ? EstadoDocumento.Faltante
            : estado;
}
