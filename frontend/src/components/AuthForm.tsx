import { useId, useState, type FormEvent, type RefObject } from 'react';
import { useSession } from '../context/SessionContext';
import { ApiError } from '../services/apiClient';
import { EMAIL_MAX_LENGTH, isValidEmail } from '../utils/checkoutValidation';

export type AuthMode = 'sign-in' | 'register';

interface AuthFormProps {
  mode: AuthMode;
  onModeChange: (mode: AuthMode) => void;
  onSuccess: () => void;
  emailInputRef: RefObject<HTMLInputElement | null>;
}

interface AuthErrors {
  email?: string;
  password?: string;
  form?: string;
}

/** Identity's default password rules, shown as a checklist. The server still decides. */
const PASSWORD_RULES: readonly { label: string; test: (value: string) => boolean }[] = [
  { label: 'At least 8 characters', test: (v) => v.length >= 8 },
  { label: 'An uppercase and a lowercase letter', test: (v) => /[A-Z]/.test(v) && /[a-z]/.test(v) },
  { label: 'A number', test: (v) => /\d/.test(v) },
  { label: 'A symbol, such as ! or #', test: (v) => /[^A-Za-z0-9]/.test(v) },
];

/** Maps the API's error to the field it belongs to, in words a shopper can act on. */
function describeAuthError(error: unknown, mode: AuthMode): AuthErrors {
  if (!(error instanceof ApiError)) return { form: 'Something went wrong. Try again.' };

  if (error.status === 401) {
    return error.problem?.detail === 'LockedOut'
      ? { form: 'Too many failed attempts. This account is locked for a few minutes; try again later.' }
      : { form: 'That email and password don’t match an account. Check them and try again.' };
  }

  if (error.code === 'VALIDATION_FAILED' && error.problem?.errors) {
    const result: AuthErrors = {};
    const passwordMessages: string[] = [];
    for (const [key, messages] of Object.entries(error.problem.errors)) {
      const message = messages[0];
      if (!message) continue;
      if (key === 'DuplicateEmail' || key === 'DuplicateUserName') {
        result.email = 'An account with this email already exists. Sign in instead.';
      } else if (key.startsWith('InvalidEmail') || key.startsWith('InvalidUserName') || key.toLowerCase() === 'email') {
        result.email ??= 'Enter a valid email address.';
      } else if (key.startsWith('Password') || key.toLowerCase() === 'password') {
        passwordMessages.push(message);
      } else {
        result.form ??= message;
      }
    }
    if (passwordMessages.length) result.password = passwordMessages.join(' ');
    if (!result.email && !result.password && !result.form) result.form = 'Check your details and try again.';
    return result;
  }

  // Rate limiting, network failures and outages: the client's message already says what to do.
  return { form: mode === 'register' && error.status >= 500 ? `${error.message} Your account may not have been created.` : error.message };
}

export function AuthForm({ mode, onModeChange, onSuccess, emailInputRef }: AuthFormProps) {
  const { signIn, register } = useSession();
  const ids = { email: useId(), password: useId(), rules: useId(), error: useId() };
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [errors, setErrors] = useState<AuthErrors>({});
  const [submitting, setSubmitting] = useState(false);
  const isRegister = mode === 'register';

  const validate = (): AuthErrors => {
    const result: AuthErrors = {};
    if (!email.trim()) result.email = 'Enter your email address.';
    else if (!isValidEmail(email.trim())) result.email = 'Enter a valid email address, like name@example.com.';
    if (!password) result.password = 'Enter your password.';
    else if (isRegister && !PASSWORD_RULES.every((rule) => rule.test(password)))
      result.password = 'Choose a password that meets all the requirements below.';
    return result;
  };

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (submitting) return;
    const found = validate();
    setErrors(found);
    if (Object.keys(found).length > 0) return;

    setSubmitting(true);
    try {
      const credentials = { email: email.trim(), password };
      await (isRegister ? register(credentials) : signIn(credentials));
      onSuccess();
    } catch (error) {
      setErrors(describeAuthError(error, mode));
      setSubmitting(false);
    }
  };

  const switchMode = (next: AuthMode) => {
    setErrors({});
    onModeChange(next);
  };

  return (
    <form className="auth-form" onSubmit={handleSubmit} noValidate aria-describedby={errors.form ? ids.error : undefined}>
      <p className="auth-form__intro">
        {isRegister
          ? 'An account keeps a history of the orders you place while signed in. You can still check out as a guest.'
          : 'Sign in to save your orders to your account. You can still check out as a guest.'}
      </p>

      <fieldset className="auth-form__fields" disabled={submitting}>
        <legend className="visually-hidden">{isRegister ? 'Create an account' : 'Sign in'}</legend>
        <div className={`field${errors.email ? ' field--invalid' : ''}`}>
          <label htmlFor={ids.email}>Email</label>
          <input
            ref={emailInputRef}
            id={ids.email}
            name="email"
            type="email"
            inputMode="email"
            autoComplete={isRegister ? 'email' : 'username'}
            autoCapitalize="none"
            spellCheck={false}
            maxLength={EMAIL_MAX_LENGTH}
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            aria-invalid={Boolean(errors.email)}
            aria-describedby={errors.email ? `${ids.email}-error` : undefined}
            required
          />
          {errors.email && (
            <p id={`${ids.email}-error`} className="field__error">
              {errors.email}
              {errors.email.includes('already exists') && (
                <>
                  {' '}
                  <button type="button" className="link-button link-button--accent" onClick={() => switchMode('sign-in')}>
                    Go to sign in
                  </button>
                </>
              )}
            </p>
          )}
        </div>

        <div className={`field${errors.password ? ' field--invalid' : ''}`}>
          <label htmlFor={ids.password}>Password</label>
          <div className="password-input">
            <input
              id={ids.password}
              name="password"
              type={showPassword ? 'text' : 'password'}
              autoComplete={isRegister ? 'new-password' : 'current-password'}
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              aria-invalid={Boolean(errors.password)}
              aria-describedby={
                [errors.password ? `${ids.password}-error` : '', isRegister ? ids.rules : ''].filter(Boolean).join(' ') || undefined
              }
              required
            />
            <button
              type="button"
              className="password-input__toggle"
              onClick={() => setShowPassword((v) => !v)}
              aria-pressed={showPassword}
              aria-controls={ids.password}
            >
              {showPassword ? 'Hide' : 'Show'}
              <span className="visually-hidden"> password</span>
            </button>
          </div>
          {errors.password && (
            <p id={`${ids.password}-error`} className="field__error">
              {errors.password}
            </p>
          )}
          {isRegister && (
            <ul id={ids.rules} className="password-rules" aria-label="Password requirements">
              {PASSWORD_RULES.map((rule) => {
                const met = rule.test(password);
                return (
                  <li key={rule.label} className={met ? 'is-met' : undefined}>
                    <span aria-hidden="true">{met ? '✓' : '•'}</span> {rule.label}
                    <span className="visually-hidden">{met ? ' (done)' : ' (not yet)'}</span>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      </fieldset>

      {errors.form && (
        <p id={ids.error} className="alert alert--error" role="alert">
          {errors.form}
        </p>
      )}

      <button type="submit" className="button button--primary button--large" disabled={submitting}>
        {submitting ? (
          <>
            <span className="spinner" aria-hidden="true" /> {isRegister ? 'Creating account…' : 'Signing in…'}
          </>
        ) : isRegister ? (
          'Create account'
        ) : (
          'Sign in'
        )}
      </button>

      <p className="auth-form__switch">
        {isRegister ? 'Already have an account?' : 'New here?'}{' '}
        <button type="button" className="link-button link-button--accent" onClick={() => switchMode(isRegister ? 'sign-in' : 'register')}>
          {isRegister ? 'Sign in' : 'Create an account'}
        </button>
      </p>
    </form>
  );
}
