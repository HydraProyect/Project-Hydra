namespace CaeManager.Application.Common;

/// <summary>
/// Marcador: este comando es propio del servicio Inbound (DN-21, punto 3). <see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/>
/// solo lo deja pasar en el Tenant de origen o con papel <see cref="PapelActivo.Inbound"/>; con papel Outbound, por la
/// vía heredada o sin papel, falla cerrado.
///
/// <para>
/// Lo llevan todos los comandos de escritura del lado del Tenant TCE de <c>CaeManager.Application.Inbound</c>, y ninguno
/// de fuera; lo exige <c>ComandosInboundConGuardaDeServicioTests</c> (Architecture.Tests). No lo llevan los del plano de
/// Plataforma (la línea Inbound del Producto Contratado, bajo Sesión Privilegiada de Aprovisionamiento) ni los del lado
/// del Tenant TEC (autorizaciones de lectura de sus documentos), que hace su Administrador propio o el Operador CAE
/// externo que lo opera en Outbound (DN-2): marcarlos los negaba con papel Outbound (corregido en B-03).
/// </para>
///
/// <para>Sin miembros a propósito, como los demás marcadores de comando.</para>
/// </summary>
public interface IComandoInbound;
