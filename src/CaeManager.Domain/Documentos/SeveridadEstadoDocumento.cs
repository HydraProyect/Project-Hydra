namespace CaeManager.Domain.Documentos;

/// <summary>
/// Orden de gravedad de un <see cref="EstadoDocumento"/> cuando hay que quedarse con el peor de varios o
/// listar primero lo que más urge. Es el ÚNICO sitio donde vive ese orden: lo que falta, antes que lo vencido,
/// antes que lo vencido pero en tolerancia (<see cref="EstadoDocumento.EnTolerancia"/>: sigue valiendo para acceder, pero ya
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
        EstadoDocumento.Faltante => 0,
        EstadoDocumento.Vencido => 1,
        EstadoDocumento.EnTolerancia => 2,
        EstadoDocumento.Urgente => 3,
        EstadoDocumento.Proximo => 4,
        EstadoDocumento.SinConfirmar => 5,
        EstadoDocumento.Vigente => 6,
        EstadoDocumento.SinCaducidad => 7,
        _ => 8
    };
}
