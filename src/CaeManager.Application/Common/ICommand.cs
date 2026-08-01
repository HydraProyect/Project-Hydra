using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Raíz común de <see cref="ICommand"/> e <see cref="ICommand{TRespuesta}"/>,
/// para poder reconocer un Command sin conocer su tipo de respuesta — que es
/// justo lo que necesita
/// <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>.
///
/// Existe como interfaz aparte y no como herencia entre las otras dos porque
/// <c>ICommand&lt;T&gt;</c> no puede heredar de <c>ICommand</c>: eso le haría
/// implementar a la vez <c>IRequest&lt;Result&gt;</c> e
/// <c>IRequest&lt;Result&lt;T&gt;&gt;</c>, y MediatR ya no sabría qué handler
/// resolver.
///
/// Ningún Command la implementa directamente.
/// </summary>
public interface ICommandBase;

/// <summary>
/// Marca un request de MediatR como <b>escritura</b>. Sustituye a la
/// convención <c>Name.EndsWith("Command")</c> con la que
/// <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/> decidía
/// antes qué autorizar: un typo en el nombre de la clase desactivaba la
/// autorización en silencio, y no había forma de que el compilador ni un
/// test lo detectaran.
///
/// La convención de nombre no desaparece —sigue siendo obligatoria por
/// <c>CODING_STANDARDS.md</c>— pero deja de ser el mecanismo de seguridad:
/// ahora es <c>ArquitecturaCommandsTests</c> quien exige que nombre e
/// interfaz vayan siempre juntos, en ambas direcciones.
///
/// El tipo de respuesta forma parte del contrato a propósito: todo Command
/// devuelve <see cref="Result"/> o <see cref="Result{T}"/>, así que ahora lo
/// garantiza el compilador en vez de una convención escrita. Es lo que
/// permite a <c>AutorizacionEscrituraBehavior</c> construir un fallo para
/// cualquier Command sin poder encontrarse un tipo de respuesta que no sabe
/// fabricar (antes, los dos Commands de importación devolvían un DTO crudo y
/// habrían hecho lanzar al behavior si un rol de solo lectura los despachaba).
/// </summary>
public interface ICommand : ICommandBase, IRequest<Result>;

/// <inheritdoc cref="ICommand"/>
public interface ICommand<TRespuesta> : ICommandBase, IRequest<Result<TRespuesta>>;
