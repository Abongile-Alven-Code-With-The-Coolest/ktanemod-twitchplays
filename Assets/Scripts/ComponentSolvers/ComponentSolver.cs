﻿using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

public abstract class ComponentSolver : ICommandResponder
{
    public delegate IEnumerator RegexResponse(Match match);

    #region Constructors
    static ComponentSolver()
    {
        _selectableType = ReflectionHelper.FindType("Selectable");
        _interactMethod = _selectableType.GetMethod("HandleInteract", BindingFlags.Public | BindingFlags.Instance);
        _interactEndedMethod = _selectableType.GetMethod("OnInteractEnded", BindingFlags.Public | BindingFlags.Instance);
        _setHighlightMethod = _selectableType.GetMethod("SetHighlight", BindingFlags.Public | BindingFlags.Instance);
        _getFocusDistanceMethod = _selectableType.GetMethod("GetFocusDistance", BindingFlags.Public | BindingFlags.Instance);

        Type thisType = typeof(ComponentSolver);
        _onPassInternalMethod = thisType.GetMethod("OnPass", BindingFlags.NonPublic | BindingFlags.Instance);
        _onStrikeInternalMethod = thisType.GetMethod("OnStrike", BindingFlags.NonPublic | BindingFlags.Instance);
    }

    public ComponentSolver(BombCommander bombCommander, MonoBehaviour bombComponent, IRCConnection ircConnection, CoroutineCanceller canceller)
    {
        BombCommander = bombCommander;
        BombComponent = bombComponent;
        Selectable = (MonoBehaviour)bombComponent.GetComponent(_selectableType);
        IRCConnection = ircConnection;
        Canceller = canceller;
    
        HookUpEvents();
    }
    #endregion

    #region Interface Implementation
    public IEnumerator RespondToCommand(string userNickName, string message, ICommandResponseNotifier responseNotifier)
    {
        if (Solved)
        {
            responseNotifier.ProcessResponse(CommandResponse.NoResponse);
            yield break;
        }

        _currentResponseNotifier = responseNotifier;
        _currentUserNickName = userNickName;

        IEnumerator subcoroutine = RespondToCommandCommon(message);
        if (subcoroutine == null || !subcoroutine.MoveNext())
        {
            subcoroutine = RespondToCommandInternal(message);
            if (subcoroutine == null || !subcoroutine.MoveNext())
            {
                responseNotifier.ProcessResponse(CommandResponse.NoResponse);
                _currentResponseNotifier = null;
                _currentUserNickName = null;
                yield break;
            }
        }

        responseNotifier.ProcessResponse(CommandResponse.Start);

        IEnumerator focusCoroutine = BombCommander.Focus(Selectable, FocusDistance, FrontFace);
        while (focusCoroutine.MoveNext())
        {
            yield return focusCoroutine.Current;
        }

        yield return new WaitForSeconds(0.5f);

        int previousStrikeCount = StrikeCount;
        bool parseError = false;
        bool needQuaternionReset = false;

        while (subcoroutine.MoveNext())
        {
            object currentValue = subcoroutine.Current;
            if (currentValue is string)
            {
                string currentString = (string)currentValue;
                if (currentString.Equals("strike", StringComparison.InvariantCultureIgnoreCase))
                {
                    _delegatedStrikeUserNickName = userNickName;
                    _delegatedStrikeResponseNotifier = responseNotifier;
                }
                else if (currentString.Equals("solve", StringComparison.InvariantCultureIgnoreCase))
                {
                    _delegatedSolveUserNickName = userNickName;
                    _delegatedSolveResponseNotifier = responseNotifier;
                }
                else if (currentString.Equals("parseerror", StringComparison.InvariantCultureIgnoreCase))
                {
                    parseError = true;
                    break;
                }
                else if (currentString.Equals("trycancel", StringComparison.InvariantCultureIgnoreCase) && Canceller.ShouldCancel)
                {
                    Canceller.ResetCancel();
                    break;
                }
                else if (currentString.StartsWith("sendtochat ", StringComparison.InvariantCultureIgnoreCase) && currentString.Substring(11).Trim() != string.Empty)
                {
                    IRCConnection.SendMessage(currentString.Substring(11));
                }
                else if (currentString.Equals("add strike"))
                {
                    _strikeCount++;
                }
                else if (currentString.Equals("award strikes"))
                {
                    AwardStrikes(_currentUserNickName, _currentResponseNotifier, StrikeCount - previousStrikeCount);
                    previousStrikeCount = StrikeCount;
                }
            }
            else if (currentValue is Quaternion)
            {
                Quaternion localQuaternion = (Quaternion)currentValue;
                BombCommander.RotateByLocalQuaternion(localQuaternion);
                needQuaternionReset = true;
            }
            yield return currentValue;
        }

        if (needQuaternionReset)
        {
            BombCommander.RotateByLocalQuaternion(Quaternion.identity);
        }

        if (parseError)
        {
            responseNotifier.ProcessResponse(CommandResponse.NoResponse);
        }
        else
        {
            if (!Solved && (previousStrikeCount == StrikeCount))
            {
                responseNotifier.ProcessResponse(CommandResponse.EndNotComplete);
            }

            yield return new WaitForSeconds(0.5f);
        }

        IEnumerator defocusCoroutine = BombCommander.Defocus(FrontFace);
        while (defocusCoroutine.MoveNext())
        {
            yield return defocusCoroutine.Current;
        }

        yield return new WaitForSeconds(0.5f);

        _currentResponseNotifier = null;
        _currentUserNickName = null;
    }
    #endregion

    #region Abstract Interface
    protected abstract IEnumerator RespondToCommandInternal(string inputCommand);
    #endregion

    #region Protected Helper Methods
    protected void DoInteractionStart(MonoBehaviour interactable)
    {
        MonoBehaviour selectable = (MonoBehaviour)interactable.GetComponent(_selectableType);
        _interactMethod.Invoke(selectable, null);
    }

    protected void DoInteractionEnd(MonoBehaviour interactable)
    {
        MonoBehaviour selectable = (MonoBehaviour)interactable.GetComponent(_selectableType);
        _interactEndedMethod.Invoke(selectable, null);
        _setHighlightMethod.Invoke(selectable, new object[] { false });
    }
    #endregion

    #region Private Methods
    private void HookUpEvents()
    {
        Delegate gameOnPassDelegate = (Delegate)CommonReflectedTypeInfo.OnPassField.GetValue(BombComponent);
        Delegate internalOnPassDelegate = Delegate.CreateDelegate(CommonReflectedTypeInfo.PassEventType, this, _onPassInternalMethod);
        CommonReflectedTypeInfo.OnPassField.SetValue(BombComponent, Delegate.Combine(internalOnPassDelegate, gameOnPassDelegate));

        Delegate gameOnStrikeDelegate = (Delegate)CommonReflectedTypeInfo.OnStrikeField.GetValue(BombComponent);
        Delegate internalOnStrikeDelegate = Delegate.CreateDelegate(CommonReflectedTypeInfo.StrikeEventType, this, _onStrikeInternalMethod);
        CommonReflectedTypeInfo.OnStrikeField.SetValue(BombComponent, Delegate.Combine(internalOnStrikeDelegate, gameOnStrikeDelegate));
    }

    private bool OnPass(object _ignore)
    {
        if (_delegatedSolveUserNickName != null && _delegatedSolveResponseNotifier != null)
        {
            AwardSolve(_delegatedSolveUserNickName, _delegatedSolveResponseNotifier);
            _delegatedSolveUserNickName = null;
            _delegatedSolveResponseNotifier = null;
        }
        else if (_currentUserNickName != null && _currentResponseNotifier != null)
        {
            AwardSolve(_currentUserNickName, _currentResponseNotifier);
        }

        return false;
    }

    private bool OnStrike(object _ignore)
    {
        _strikeCount++;

        if (_delegatedStrikeUserNickName != null && _delegatedStrikeResponseNotifier != null)
        {
            AwardStrikes(_delegatedStrikeUserNickName, _delegatedStrikeResponseNotifier, 1);
            _delegatedStrikeUserNickName = null;
            _delegatedStrikeResponseNotifier = null;
        }
        else if (_currentUserNickName != null && _currentResponseNotifier != null)
        {
            AwardStrikes(_currentUserNickName, _currentResponseNotifier, 1);
        }

        return false;
    }

    private void AwardSolve(string userNickName, ICommandResponseNotifier responseNotifier)
    {
        IRCConnection.SendMessage(string.Format("VoteYea Module {0} is solved! +1 solve to {1}", Code, userNickName));
        responseNotifier.ProcessResponse(CommandResponse.EndComplete);
    }

    private void AwardStrikes(string userNickName, ICommandResponseNotifier responseNotifier, int strikeCount)
    {
        IRCConnection.SendMessage(string.Format("VoteNay Module {0} got {1} strike{2}! +{3} strike{2} to {4}", Code, strikeCount == 1 ? "a" : strikeCount.ToString(), strikeCount == 1 ? "" : "s", strikeCount, userNickName));
        responseNotifier.ProcessResponse(CommandResponse.EndError, strikeCount);
    }
    #endregion

    public string Code
    {
        get;
        set;
    }
    
    #region Protected Properties
    protected bool Solved
    {
        get
        {
            return (bool)CommonReflectedTypeInfo.IsSolvedField.GetValue(BombComponent);
        }
    }

    protected bool Detonated
    {
        get
        {
            return (bool)CommonReflectedTypeInfo.HasDetonatedProperty.GetValue(BombCommander.Bomb, null);
        }
    }

    private int _strikeCount = 0;
    protected int StrikeCount
    {
        get
        {
            return _strikeCount;
        }
    }

    protected float FocusDistance
    {
        get
        {
            MonoBehaviour selectable = (MonoBehaviour)BombComponent.GetComponent(_selectableType);
            return (float)_getFocusDistanceMethod.Invoke(selectable, null);
        }
    }

    protected bool FrontFace
    {
        get
        {
            Vector3 componentUp = BombComponent.transform.up;
            Vector3 bombUp = BombCommander.Bomb.transform.up;
            float angleBetween = Vector3.Angle(componentUp, bombUp);
            return angleBetween < 90.0f;
        }
    }

    protected FieldInfo TryCancelField { get; set; }
    protected Type TryCancelComponentSolverType { get; set; }

    protected bool TryCancel
    {
        get
        {
            if (TryCancelField == null || TryCancelComponentSolverType == null ||
                !(TryCancelField.GetValue(TryCancelComponentSolverType) is bool))
                return false;
            return (bool)TryCancelField.GetValue(BombComponent.GetComponent(TryCancelComponentSolverType));
        }
        set
        {
            if (TryCancelField != null && TryCancelComponentSolverType != null &&
                (TryCancelField.GetValue(BombComponent.GetComponent(TryCancelComponentSolverType)) is bool))
                TryCancelField.SetValue(BombComponent.GetComponent(TryCancelComponentSolverType), value);
        }
    }
    #endregion

    #region Private Methods
    private IEnumerator RespondToCommandCommon(string inputCommand)
    {
        if (inputCommand.Equals("show", StringComparison.InvariantCultureIgnoreCase))
        {
            yield return "show";
            yield return null;
        }
    }
    #endregion

    #region Readonly Fields
    protected readonly BombCommander BombCommander = null;
    protected readonly MonoBehaviour BombComponent = null;
    protected readonly MonoBehaviour Selectable = null;
    protected readonly IRCConnection IRCConnection = null;
    public readonly CoroutineCanceller Canceller = null;
    #endregion

    #region Private Static Fields
    private static Type _selectableType = null;
    private static MethodInfo _interactMethod = null;
    private static MethodInfo _interactEndedMethod = null;
    private static MethodInfo _setHighlightMethod = null;
    private static MethodInfo _getFocusDistanceMethod = null;

    private static MethodInfo _onPassInternalMethod = null;
    private static MethodInfo _onStrikeInternalMethod = null;
    #endregion

    #region Private Fields
    private ICommandResponseNotifier _delegatedStrikeResponseNotifier = null;
    private string _delegatedStrikeUserNickName = null;

    private ICommandResponseNotifier _delegatedSolveResponseNotifier = null;
    private string _delegatedSolveUserNickName = null;

    private ICommandResponseNotifier _currentResponseNotifier = null;
    private string _currentUserNickName = null;
    #endregion
    
    public string helpMessage = null;
    public string manualCode = null;
}
