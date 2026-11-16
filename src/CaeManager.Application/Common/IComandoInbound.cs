namespace CaeManager.Application.Common;

/// <summary>
/// Marcador: este comando es propio del servicio Inbound (DN-21, punto 3). <see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/>
/// solo lo deja pasar en el Tenant de origen o con papel <see cref="PapelActivo.Inbound"/>; con papel Outbound, por la
/// vía heredada o sin papel, falla cerrado.
///
/// <para>
/// Lo llevan todos los comandos de escritura de <c>CaeManager.Application.Inbound</c> salvo los del plano de
/// Plataforma (la línea Inbound del Producto Contratado, bajo Sesión Privilegiada de Aprovisionamiento), y ninguno de
/// fuera; lo exige <c>ComandosInboundConGuardaDeServicioTests</c> (Architecture.Tests).
/// </para>
///
/// <para>Sin miembros a propósito, como los demás marcadores de comando.</para>
/// </summary>
public interface IComandoInbound;
