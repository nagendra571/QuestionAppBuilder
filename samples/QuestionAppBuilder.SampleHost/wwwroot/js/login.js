(() => {
    const form = document.getElementById('login-form');
    const username = document.getElementById('Username');
    const password = document.getElementById('Password');
    const toggle = document.querySelector('.password-toggle');
    const roles = document.querySelectorAll('.demo-role');
    toggle.hidden = false;
    toggle.addEventListener('click', () => {
        const show = password.type === 'password';
        password.type = show ? 'text' : 'password';
        toggle.textContent = show ? 'Hide' : 'Show';
        toggle.setAttribute('aria-label', show ? 'Hide password' : 'Show password');
        toggle.setAttribute('aria-pressed', String(show));
    });
    roles.forEach(button => {
        button.setAttribute('aria-pressed', 'false');
        button.addEventListener('click', () => {
            username.value = button.dataset.username;
            password.value = document.getElementById('demo-password').textContent.trim();
            roles.forEach(role => role.setAttribute('aria-pressed', String(role === button)));
            document.getElementById('role-status').textContent = button.dataset.username + ' credentials filled. Select Sign in to continue.';
            form.querySelector('[type="submit"]').focus();
        });
    });
    [username, password].forEach(input => input.addEventListener('input', () => {
        roles.forEach(role => role.setAttribute('aria-pressed', 'false'));
    }));
    form.addEventListener('submit', () => {
        const submit = form.querySelector('[type="submit"]');
        submit.disabled = true;
        submit.querySelector('span').textContent = 'Signing in…';
    });
    window.addEventListener('pageshow', () => {
        const submit = form.querySelector('[type="submit"]');
        submit.disabled = false;
        submit.querySelector('span').textContent = 'Sign in';
    });
})();
