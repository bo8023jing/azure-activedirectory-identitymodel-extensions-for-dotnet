//------------------------------------------------------------------------------
//
// Copyright (c) Microsoft Corporation.
// All rights reserved.
//
// This code is licensed under the MIT License.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
//------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Xml;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Xml;
using static Microsoft.IdentityModel.Logging.LogHelper;

using TokenLogMessages = Microsoft.IdentityModel.Tokens.LogMessages;

namespace Microsoft.IdentityModel.Tokens.Saml2
{
    /// <summary>
    /// A <see cref="SecurityTokenHandler"/> designed for creating and validating Saml2 Tokens. See: http://docs.oasis-open.org/security/saml/v2.0/saml-core-2.0-os.pdf
    /// </summary>
    public class Saml2SecurityTokenHandler : SecurityTokenHandler, ISecurityTokenValidator
    {
        private const string Actor = "Actor";
        private int _maximumTokenSizeInBytes = TokenValidationParameters.DefaultMaximumTokenSizeInBytes;
        private TransformFactory _transformFactory = TransformFactory.Default;

        /// <summary>
        /// Initializes a new instance of <see cref="Saml2SecurityTokenHandler"/>.
        /// </summary>
        public Saml2SecurityTokenHandler()
        { }

        /// <summary>
        /// Gets or set the <see cref="Saml2Serializer"/> that will be used to read and write a <see cref="Saml2SecurityToken"/>.
        /// </summary>
        public Saml2Serializer Serializer { get; set; } = new Saml2Serializer();

        /// <summary>
        /// Gets and sets TransformFactory
        /// </summary>
        /// <exception cref="ArgumentNullException">'value' is null.</exception>
        public TransformFactory TransformFactory
        {
            get
            {
                return _transformFactory;
            }
            set
            {
                _transformFactory = value ?? throw LogArgumentNullException(nameof(value));
            }
        }

        /// <summary>
        /// Returns a value that indicates if this handler can validate a <see cref="SecurityToken"/>.
        /// </summary>
        /// <returns>'true', indicating this instance can validate a <see cref="Saml2SecurityToken"/>.</returns>
        public override bool CanValidateToken
        {
            get { return true; }
        }

        /// <summary>
        /// Gets the token type supported by this handler.
        /// </summary>
        public override Type TokenType
        {
            get { return typeof(Saml2SecurityToken); }
        }

        /// <summary>
        /// Gets the value that indicates if this instance can write a <see cref="SecurityToken"/>.
        /// </summary>
        /// <returns>'true', indicating this instance can write a <see cref="Saml2SecurityToken"/>.</returns>
        public override bool CanWriteToken
        {
            get { return true; }
        }

        /// <summary>
        /// Gets and sets the maximum token size in bytes that will be processed.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">'value' less than 1.</exception>
        public int MaximumTokenSizeInBytes
        {
            get { return _maximumTokenSizeInBytes; }
            set
            {
                if (value < 1)
                    throw LogExceptionMessage(new ArgumentOutOfRangeException(nameof(value), FormatInvariant(TokenLogMessages.IDX10101, value)));

                _maximumTokenSizeInBytes = value;
            }
        }

        /// <summary>
        /// Determines if the string is a valid Saml2 token by examining the xml for the correct start element.
        /// </summary>
        /// <param name="token">A Saml2 token as a string.</param>
        /// <returns>'true' if the string has a start element equal <see cref="Saml2Constants.Elements.Assertion"/>.</returns>
        public override bool CanReadToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            if (token.Length > MaximumTokenSizeInBytes)
                return false;

            try
            {
                using (var sr = new StringReader(token))
                {
                    using (var reader = XmlDictionaryReader.CreateDictionaryReader(XmlReader.Create(sr)))
                    {
                        return CanReadToken(reader);
                    }
                }
            }
            catch(Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Indicates whether the current XML element can be read as a token of the type handled by this instance.
        /// </summary>
        /// <param name="reader">An <see cref="XmlReader"/> reader positioned at a start element. The reader should not be advanced.</param>
        /// <returns>'true' if <see cref="Saml2SecurityTokenHandler.ReadToken(string)"/> can read the element.</returns>
        public bool CanReadToken(XmlReader reader)
        {
            if (reader == null)
                return false;

            return reader.IsStartElement(Saml2Constants.Elements.Assertion, Saml2Constants.Namespace);
        }

        /// <summary>
        /// Creates a <see cref="Saml2SecurityToken"/>.
        /// </summary>
        /// <param name="tokenDescriptor">The <see cref="SecurityTokenDescriptor"/> that has creation information.</param>
        /// <returns>A <see cref="SecurityToken"/> instance.</returns>
        /// <exception cref="ArgumentNullException">If 'tokenDescriptor' is null.</exception>
        public override SecurityToken CreateToken(SecurityTokenDescriptor tokenDescriptor)
        {
            if (tokenDescriptor == null)
                throw LogArgumentNullException(nameof(tokenDescriptor));

            // Assertion/issuer
            var assertion = new Saml2Assertion(CreateIssuerNameIdentifier(tokenDescriptor))
            {
                Subject = CreateSamlSubject(tokenDescriptor),
                SigningCredentials = GetSigningCredentials(tokenDescriptor),
                Conditions = CreateConditions(tokenDescriptor),
                Advice = CreateAdvice(tokenDescriptor)
            };

            // Statements
            IEnumerable<Saml2Statement> statements = CreateStatements(tokenDescriptor);
            if (statements != null)
            {
                foreach (var statement in statements)
                {
                    assertion.Statements.Add(statement);
                }
            }

            // TODO - encryption not currently supported
            // encrypting credentials
            // assertion.EncryptingCredentials = GetEncryptingCredentials(tokenDescriptor);

            return new Saml2SecurityToken(assertion);
        }

        /// <summary>
        /// Reads and validates a <see cref="Saml2SecurityToken"/>.
        /// </summary>
        /// <param name="token">The Saml2 token.</param>
        /// <param name="validationParameters">Contains validation parameters for the <see cref="Saml2SecurityToken"/>.</param>
        /// <param name="validatedToken">The <see cref="Saml2SecurityToken"/> that was validated.</param>
        /// <exception cref="ArgumentNullException">'token' is null or whitespace.</exception>
        /// <exception cref="ArgumentNullException">'validationParameters' is null.</exception>
        /// <exception cref="ArgumentException">token.Length is greater than <see cref="Saml2SecurityTokenHandler.MaximumTokenSizeInBytes"/>.</exception>
        /// <exception cref="Saml2SecurityTokenReadException">if the token is not well-formed.</exception>
        /// <returns>A <see cref="ClaimsPrincipal"/> representing the identity contained in the token.</returns>
        public virtual ClaimsPrincipal ValidateToken(string token, TokenValidationParameters validationParameters, out SecurityToken validatedToken)
        {
            if (token == null)
                throw LogArgumentNullException(nameof(token));

            if (validationParameters == null)
                throw LogArgumentNullException(nameof(validationParameters));

            if (token.Length > MaximumTokenSizeInBytes)
                throw LogExceptionMessage(new ArgumentException(FormatInvariant(TokenLogMessages.IDX10209, token.Length, MaximumTokenSizeInBytes)));

            var samlToken = ValidateSignature(token, validationParameters);
            ValidateConditions(samlToken, validationParameters);
            ValidateSubject(samlToken, validationParameters);
            var issuer = ValidateIssuer(samlToken.Issuer, samlToken, validationParameters);
            ValidateTokenReplay(samlToken.Assertion.Conditions.NotBefore, token, validationParameters);
            validatedToken = samlToken;
            var identity = CreateClaimsIdentity(samlToken, issuer, validationParameters);
            if (validationParameters.SaveSigninToken)
                identity.BootstrapContext = token;

            IdentityModelEventSource.Logger.WriteInformation(TokenLogMessages.IDX10241, token);

            return new ClaimsPrincipal(identity);
        }

        /// <summary>
        /// Validates the first SubjectConfirmationData
        /// </summary>
        /// <param name="samlToken">the Saml2 token that is being validated.</param>
        /// <param name="validationParameters">validation parameters.</param>
        /// <exception cref="ArgumentNullException">If 'securityToken' is null.</exception>
        /// <exception cref="ArgumentNullException">If 'validationParameters' is null.</exception>
        /// <exception cref="Saml2SecurityTokenException">If 'securityToken.Subject' is null.</exception>
        protected virtual void ValidateSubject(Saml2SecurityToken samlToken, TokenValidationParameters validationParameters)
        {
            if (samlToken == null)
                throw LogArgumentNullException(nameof(samlToken));

            if (samlToken.Assertion == null)
                throw LogArgumentNullException(nameof(samlToken.Assertion));

            if (samlToken.Assertion.Subject == null)
                throw LogExceptionMessage(new Saml2SecurityTokenException(LogMessages.IDX10509));

            foreach (var subjectConfirmation in samlToken.Assertion.Subject.SubjectConfirmations)
            {
                if (subjectConfirmation != null && subjectConfirmation.SubjectConfirmationData != null)
                    ValidateConfirmationData(samlToken, validationParameters, subjectConfirmation.SubjectConfirmationData);
            }
        }

        /// <summary>
        /// Validate token replay
        /// </summary>
        /// <param name="expirationTime">expiration time.</param>
        /// <param name="securityToken">the Saml2 token that is being validated.</param>
        /// <param name="validationParameters">validation parameters.</param>
        protected virtual void ValidateTokenReplay(DateTime? expirationTime, string securityToken, TokenValidationParameters validationParameters)
        {
            Validators.ValidateTokenReplay(expirationTime, securityToken, validationParameters);
        }

        /// <summary>
        /// Validates <see cref="Saml2SubjectConfirmationData"/> object for lifetime.
        /// </summary>
        /// <param name="samlToken">the <see cref="Saml2SecurityToken"/> being validated.</param>
        /// <param name="validationParameters">the <see cref="TokenValidationParameters"/> that will be used during validation.</param>
        /// <param name="confirmationData">The <see cref="Saml2SubjectConfirmationData"/> to validate.</param>
        /// <exception cref="ArgumentNullException">If 'confirmationData' is null.</exception>
        /// <exception cref="ArgumentNullException">If 'validationParameters' is null.</exception>
        /// <remarks>calls <see cref="Validators.ValidateLifetime(DateTime?, DateTime?, SecurityToken, TokenValidationParameters)"/></remarks>
        protected virtual void ValidateConfirmationData(Saml2SecurityToken samlToken, TokenValidationParameters validationParameters, Saml2SubjectConfirmationData confirmationData)
        {
            if (confirmationData == null)
                throw LogArgumentNullException(nameof(confirmationData));

            if (validationParameters == null)
                throw LogArgumentNullException(nameof(validationParameters));

            Validators.ValidateLifetime(confirmationData.NotBefore, confirmationData.NotOnOrAfter, samlToken, validationParameters);
        }

        /// <summary>
        /// Validates that the signature, if found is valid.
        /// </summary>
        /// <param name="token">A Saml2 token.</param>
        /// <param name="validationParameters"><see cref="TokenValidationParameters"/> that will be used during validation.</param>
        /// <exception cref="ArgumentNullException">If 'token' is null or whitespace.</exception>
        /// <exception cref="ArgumentNullException">If 'validationParameters' is null.</exception>
        /// <exception cref="SecurityTokenValidationException">If <see cref="TokenValidationParameters.SignatureValidator"/> returns null OR an object other than a <see cref="Saml2SecurityToken"/>.</exception>
        /// <exception cref="SecurityTokenValidationException">If a signature is not found and <see cref="TokenValidationParameters.RequireSignedTokens"/> is true.</exception>
        /// <exception cref="SecurityTokenSignatureKeyNotFoundException">If the 'token' has a key identifier and none of the <see cref="SecurityKey"/>(s) provided result in a validated signature. 
        /// This can indicate that a key refresh is required.</exception>
        /// <exception cref="SecurityTokenInvalidSignatureException">If after trying all the <see cref="SecurityKey"/>(s), none result in a validated signature AND the 'token' does not have a key identifier.</exception>
        /// <returns>A <see cref="Saml2SecurityToken"/> that has had the signature validated if token was signed.</returns>
        /// <remarks><para>If the 'token' is signed, the signature is validated even if <see cref="TokenValidationParameters.RequireSignedTokens"/> is false.</para>
        /// <para>If the 'token' signature is validated, then the <see cref="Saml2SecurityToken.SigningKey"/> will be set to the key that signed the 'token'. It is the responsibility of <see cref="TokenValidationParameters.SignatureValidator"/> to set the <see cref="Saml2SecurityToken.SigningKey"/></para></remarks>
        protected virtual Saml2SecurityToken ValidateSignature(string token, TokenValidationParameters validationParameters)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw LogArgumentNullException(nameof(token));

            if (validationParameters == null)
                throw LogArgumentNullException(nameof(validationParameters));

            if (validationParameters.SignatureValidator != null)
            {
                var validatedSamlToken = validationParameters.SignatureValidator(token, validationParameters);
                if (validatedSamlToken == null)
                    throw LogExceptionMessage(new SecurityTokenValidationException(FormatInvariant(TokenLogMessages.IDX10505, token)));

                var validatedSaml = validatedSamlToken as Saml2SecurityToken;
                if (validatedSaml == null)
                    throw LogExceptionMessage(new SecurityTokenValidationException(FormatInvariant(TokenLogMessages.IDX10506, typeof(Saml2SecurityToken), validatedSamlToken.GetType(), token)));

                return validatedSaml;
            }

            var samlToken = ReadSaml2Token(token);

            if (!validationParameters.RequireSignedTokens)
                return samlToken;

            if (samlToken.Assertion.Signature == null && validationParameters.RequireSignedTokens)
                throw LogExceptionMessage(new SecurityTokenValidationException(FormatInvariant(TokenLogMessages.IDX10504, token)));

            bool keyMatched = false;
            IEnumerable<SecurityKey> keys = null;
            if (validationParameters.IssuerSigningKeyResolver != null)
                keys = validationParameters.IssuerSigningKeyResolver(token, samlToken, samlToken.SigningKey.KeyId, validationParameters);
            else
            {
                var key = ResolveIssuerSigningKey(token, samlToken, validationParameters);
                if (key != null)
                {
                    // remember that key was matched for throwing exception SecurityTokenSignatureKeyNotFoundException
                    keyMatched = true;
                    keys = new List<SecurityKey> { key };
                }
            }

            if (keys == null)
            {
                // control gets here if:
                // 1. User specified delegate: IssuerSigningKeyResolver returned null
                // 2. ResolveIssuerSigningKey returned null
                // Try all the keys. This is the degenerate case, not concerned about perf.
                keys = GetAllSigningKeys(validationParameters);
            }

            // keep track of exceptions thrown, keys that were tried
            var exceptionStrings = new StringBuilder();
            var keysAttempted = new StringBuilder();
            bool canMatchKey = samlToken.Assertion.Signature.KeyInfo != null;

            // use the provided TransformFactory
            samlToken.Assertion.Signature.SignedInfo.TransformFactory = TransformFactory;

            foreach (var key in keys)
            {
                try
                {
                    samlToken.Assertion.Signature.Verify(key);
                    IdentityModelEventSource.Logger.WriteInformation(TokenLogMessages.IDX10242, token);
                    samlToken.SigningKey = key;
                    return samlToken;
                }
                catch (Exception ex)
                {
                    exceptionStrings.AppendLine(ex.ToString());
                }

                if (key != null)
                {
                    keysAttempted.AppendLine(key.ToString() + " , KeyId: " + key.KeyId);
                    if (canMatchKey && !keyMatched && key.KeyId != null)
                        keyMatched = key.KeyId.Equals(samlToken.Assertion.Signature.KeyInfo.Kid, StringComparison.Ordinal);
                }
            }

            // if there was a key match with what was found in tokenValidationParameters most likely metadata is stale. throw SecurityTokenSignatureKeyNotFoundException
            if (!keyMatched && canMatchKey && keysAttempted.Length > 0)
                throw LogExceptionMessage(new SecurityTokenSignatureKeyNotFoundException(FormatInvariant(TokenLogMessages.IDX10501, samlToken.Assertion.Signature.KeyInfo, samlToken)));

            if (keysAttempted.Length > 0)
                throw LogExceptionMessage(new SecurityTokenInvalidSignatureException(FormatInvariant(TokenLogMessages.IDX10503, keysAttempted, exceptionStrings, samlToken)));

            throw LogExceptionMessage(new SecurityTokenInvalidSignatureException(TokenLogMessages.IDX10500));
        }

        private IEnumerable<SecurityKey> GetAllSigningKeys(TokenValidationParameters validationParameters)
        {
            IdentityModelEventSource.Logger.WriteInformation(TokenLogMessages.IDX10243);
            if (validationParameters.IssuerSigningKey != null)
                yield return validationParameters.IssuerSigningKey;

            if (validationParameters.IssuerSigningKeys != null)
                foreach (SecurityKey key in validationParameters.IssuerSigningKeys)
                    yield return key;
        }

        /// <summary>
        /// Returns a <see cref="SecurityKey"/> to use for validating the signature of a token.
        /// </summary>
        /// <param name="token">The <see cref="string"/> representation of the token that is being validated.</param>
        /// <param name="samlToken">The <see cref="Saml2SecurityToken"/> that is being validated.</param>
        /// <param name="validationParameters"><see cref="TokenValidationParameters"/> that will be used during validation.</param>
        /// <returns>Returns a <see cref="SecurityKey"/> to use for signature validation.</returns>
        /// <exception cref="ArgumentNullException">If 'securityToken' is null.</exception>
        /// <exception cref="ArgumentNullException">If 'validationParameters' is null.</exception>
        /// <exception cref="ArgumentNullException">If 'securityToken.Assertion' is null.</exception>
        /// <remarks>If key fails to resolve, then null is returned</remarks>
        protected virtual SecurityKey ResolveIssuerSigningKey(string token, Saml2SecurityToken samlToken, TokenValidationParameters validationParameters)
        {
            if (samlToken == null)
                throw LogArgumentNullException(nameof(samlToken));

            if (validationParameters == null)
                throw LogArgumentNullException(nameof(validationParameters));

            if (samlToken.Assertion == null)
                throw LogArgumentNullException(nameof(samlToken.Assertion));

            if (samlToken.Assertion.Signature != null && samlToken.Assertion.Signature.KeyInfo != null && !string.IsNullOrEmpty(samlToken.Assertion.Signature.KeyInfo.Kid))
            {
                if (validationParameters.IssuerSigningKey != null && string.Equals(validationParameters.IssuerSigningKey.KeyId, samlToken.Assertion.Signature.KeyInfo.Kid, StringComparison.Ordinal))
                    return validationParameters.IssuerSigningKey;

                if (validationParameters.IssuerSigningKeys != null)
                {
                    foreach (var key in validationParameters.IssuerSigningKeys)
                    {
                        if (key != null && string.Equals(key.KeyId, samlToken.Assertion.Signature.KeyInfo.Kid, StringComparison.Ordinal))
                            return key;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Converts a string into an instance of <see cref="Saml2SecurityToken"/>.
        /// </summary>
        /// <param name="token">a Saml2 token as a string.</param>
        /// <exception cref="ArgumentNullException"> If 'token' is null or empty.</exception>
        /// <exception cref="ArgumentException"> If 'token.Length' $gt; <see cref="MaximumTokenSizeInBytes"/>.</exception>
        /// <returns>A <see cref="Saml2SecurityToken"/></returns>
        public virtual Saml2SecurityToken ReadSaml2Token(string token)
        {
            if (string.IsNullOrEmpty(token))
                throw LogArgumentNullException(nameof(token));

            if (token.Length > MaximumTokenSizeInBytes)
                throw LogExceptionMessage(new ArgumentException(FormatInvariant(TokenLogMessages.IDX10209, token.Length, MaximumTokenSizeInBytes)));

            using (var stringReader = new StringReader(token))
            {
                return new Saml2SecurityToken(Serializer.ReadAssertion(XmlReader.Create(stringReader)));
            }
        }

        /// <summary>
        /// Converts a string into an instance of <see cref="Saml2SecurityToken"/>.
        /// </summary>
        /// <param name="token">a Saml2 token as a string.</param>
        /// <exception cref="ArgumentNullException"> If 'token' is null or empty.</exception>
        /// <exception cref="ArgumentException"> If 'token.Length' $gt; <see cref="MaximumTokenSizeInBytes"/>.</exception>
        /// <returns>A <see cref="Saml2SecurityToken"/></returns>
        public override SecurityToken ReadToken(string token)
        {
            return ReadSaml2Token(token);
        }

        /// <summary>
        /// Reads and validates a SAML 2.0 token using the XmlReader.
        /// </summary>
        /// <param name="reader">A <see cref="XmlReader"/> reader positioned at a <see cref="Saml2Assertion"/> element.</param>
        /// <param name="validationParameters"> validation parameters for the <see cref="Saml2SecurityToken"/>.</param>
        /// <returns>An instance of <see cref="Saml2SecurityToken"/>.</returns>
        public override SecurityToken ReadToken(XmlReader reader, TokenValidationParameters validationParameters)
        {
            throw new NotSupportedException("API is not supported");
        }

#if SAML2_Encryption
        internal static XmlDictionaryReader CreatePlaintextReaderFromEncryptedData(
                        XmlDictionaryReader reader,
                        Collection<SecurityKeyIdentifierClause> clauses,
                        out EncryptingCredentials encryptingCredentials)
        {
            if (reader == null)
                throw LogArgumentNullException(nameof(reader));

            reader.MoveToContent();
            if (reader.IsEmptyElement)
                throw LogExceptionMessage(new Saml2SecurityTokenException("ID3061"));

            encryptingCredentials = null;

            XmlUtil.ValidateXsiType(reader, Saml2Constants.Types.EncryptedElementType, Saml2Constants.Namespace);

            reader.ReadStartElement();
            EncryptedDataElement encryptedData = new EncryptedDataElement();

            // <xenc:EncryptedData> 1
            encryptedData.ReadXml(reader);

            // <xenc:EncryptedKey> 0-oo
            reader.MoveToContent();
            while (reader.IsStartElement(XmlEncryptionConstants.Elements.EncryptedKey, XmlEncryptionConstants.Namespace))
            {
                reader.Skip();
                // TODO - securityKey reader / writer
                //if (keyInfoSerializer.CanReadKeyIdentifierClause(reader))
                //{
                //    skic = keyInfoSerializer.ReadKeyIdentifierClause(reader);
                //}
                //else
                //{
                //    EncryptedKeyElement encryptedKey = new EncryptedKeyElement(keyInfoSerializer);
                //    encryptedKey.ReadXml(reader);
                //    skic = encryptedKey.GetClause();
                //}

                //EncryptedKeyIdentifierClause encryptedKeyClause = skic as EncryptedKeyIdentifierClause;
                //if (encryptedKeyClause == null)
                //{
                //    throw LogHelper.ThrowHelperXml(reader, SR.GetString(SR.ID4172));
                //}

                //clauses.Add(encryptedKeyClause);
            }

            reader.ReadEndElement();

            // Try to resolve the decryption key from both the embedded 
            // KeyInfo and any external clauses
            //SecurityKey decryptionKey = null;
            //SecurityKeyIdentifierClause matchingClause = null;

            //foreach (SecurityKeyIdentifierClause clause in encryptedData.KeyIdentifier)
            //{
            //    if (serviceTokenResolver.TryResolveSecurityKey(clause, out decryptionKey))
            //    {
            //        matchingClause = clause;
            //        break;
            //    }
            //}

            //if (decryptionKey == null)
            //{
            //    foreach (SecurityKeyIdentifierClause clause in clauses)
            //    {
            //        if (serviceTokenResolver.TryResolveSecurityKey(clause, out decryptionKey))
            //        {
            //            matchingClause = clause;
            //            break;
            //        }
            //    }
            //}

            //if (decryptionKey == null)
            //{
            //    throw LogExceptionMessage(
            //        new EncryptedTokenDecryptionFailedException());
            //}

            //// Need a symmetric key
            //SymmetricSecurityKey symmetricKey = decryptionKey as SymmetricSecurityKey;
            //if (symmetricKey == null)
            //{
            //    throw LogExceptionMessage(
            //        new SecurityTokenException(SR.GetString(SR.ID4023)));
            //}

            //// Do the actual decryption
            //SymmetricAlgorithm decryptor = symmetricKey.GetSymmetricAlgorithm(encryptedData.Algorithm);
            //byte[] plainText = encryptedData.Decrypt(decryptor);

            //// Save off the encrypting credentials for roundtrip
            //encryptingCredentials = new ReceivedEncryptingCredentials(decryptionKey, new SecurityKeyIdentifier(matchingClause), encryptedData.Algorithm);

            return XmlDictionaryReader.CreateTextReader(new byte[10], reader.Quotas);
        }
#endif

        /// <summary>
        /// Indicates if the current XML element is pointing to a Saml2Assertion.
        /// </summary>
        /// <param name="reader">A reader that may contain a <see cref="Saml2Assertion"/>.</param>
        /// <returns>'true' if reader contains a <see cref="Saml2Assertion"/>. 'false' otherwise.</returns>
        internal static bool IsSaml2Assertion(XmlReader reader)
        {
            return reader.IsStartElement(Saml2Constants.Elements.Assertion, Saml2Constants.Namespace);
        }

        /// <summary>
        /// Creates the conditions for the assertion.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Generally, conditions should be included in assertions to limit the 
        /// impact of misuse of the assertion. Specifying the NotBefore and 
        /// NotOnOrAfter conditions can limit the period of vulnerability in 
        /// the case of a compromised assertion. The AudienceRestrictionCondition
        /// can be used to explicitly state the intended relying party or parties
        /// of the assertion, which coupled with appropriate audience restriction
        /// enforcement at relying parties can help to mitigate spoofing attacks
        /// between relying parties.
        /// </para>
        /// <para>
        /// The default implementation creates NotBefore and NotOnOrAfter conditions
        /// based on the tokenDescriptor.Lifetime. It will also generate an 
        /// AudienceRestrictionCondition limiting consumption of the assertion to 
        /// tokenDescriptor.Scope.Address.
        /// </para>
        /// </remarks>
        /// <param name="tokenDescriptor">contains the details of the conditions.</param>
        /// <returns>A Saml2Conditions object.</returns>
        protected virtual Saml2Conditions CreateConditions(SecurityTokenDescriptor tokenDescriptor)
        {
            if (tokenDescriptor == null)
                throw LogArgumentNullException(nameof(tokenDescriptor));

            var conditions = new Saml2Conditions();
            if (tokenDescriptor.NotBefore.HasValue)
                conditions.NotBefore = tokenDescriptor.NotBefore.Value;

            if (tokenDescriptor.Expires.HasValue)
                conditions.NotOnOrAfter = tokenDescriptor.Expires.Value;

            if (!string.IsNullOrEmpty(tokenDescriptor.Audience))
                conditions.AudienceRestrictions.Add(new Saml2AudienceRestriction(tokenDescriptor.Audience));

            return conditions;
        }

        /// <summary>
        /// Creates the advice for the assertion.
        /// </summary>
        /// <remarks>
        /// By default, this method returns null.
        /// </remarks>
        /// <param name="tokenDescriptor">The token descriptor.</param>
        /// <returns>A <see cref="Saml2Advice"/> object, default is null.</returns>
        protected virtual Saml2Advice CreateAdvice(SecurityTokenDescriptor tokenDescriptor)
        {
            return null;
        }

        /// <summary>
        /// Creates a name identifier that identifies the assertion issuer.
        /// </summary>
        /// <param name="tokenDescriptor">The token descriptor.</param>
        /// <returns>A <see cref="Saml2NameIdentifier"/> from the 'tokenDescriptor'.</returns>
        /// <exception cref="ArgumentNullException"> if 'tokenDescriptor' is null.</exception>
        /// <exception cref="Saml2SecurityTokenException"> if 'tokenDescriptor.Issuer' is null or empty.</exception>
        /// <remarks>Uses tokenDescriptor.Issuer.</remarks>
        protected virtual Saml2NameIdentifier CreateIssuerNameIdentifier(SecurityTokenDescriptor tokenDescriptor)
        {
            if (tokenDescriptor == null)
                throw LogArgumentNullException(nameof(tokenDescriptor));

            // Must have an issuer
            if (string.IsNullOrEmpty(tokenDescriptor.Issuer))
                throw LogExceptionMessage(new Saml2SecurityTokenException("ID4138"));

            return new Saml2NameIdentifier(tokenDescriptor.Issuer);
        }

        /// <summary>
        /// Generates a Saml2Attribute from a claim.
        /// </summary>
        /// <param name="claim">The <see cref="Claim"/> from which to generate a <see cref="Saml2Attribute"/>.</param>
        /// <param name="tokenDescriptor">Contains all the information that is used in token issuance.</param>
        /// <returns>A <see cref="Saml2Attribute"/> based on the claim.</returns>
        /// <exception cref="ArgumentNullException">The parameter 'claim' is null.</exception>
        /// <exception cref="Saml2SecurityTokenException">If the 'claim' has a property "ClaimsProperties.SamlAttributeNameFormat" and the value is not a valid absolute URI.</exception>
        protected virtual Saml2Attribute CreateAttribute(Claim claim, SecurityTokenDescriptor tokenDescriptor)
        {
            if (claim == null)
                throw LogArgumentNullException(nameof(claim));

            var attribute = new Saml2Attribute(claim.Type, claim.Value);
            if (!StringComparer.Ordinal.Equals(claim.Issuer, claim.OriginalIssuer))
                attribute.OriginalIssuer = claim.OriginalIssuer;

            attribute.AttributeValueXsiType = claim.ValueType;
            if (claim.Properties.ContainsKey(ClaimProperties.SamlAttributeNameFormat))
            {
                string nameFormat = claim.Properties[ClaimProperties.SamlAttributeNameFormat];
                if (!Saml2Serializer.CanCreateValidUri(nameFormat, UriKind.Absolute))
                    throw LogExceptionMessage(new Saml2SecurityTokenException(FormatInvariant(LogMessages.IDX11300, ClaimProperties.SamlAttributeNameFormat)));

                attribute.NameFormat = new Uri(nameFormat);
            }

            if (claim.Properties.ContainsKey(ClaimProperties.SamlAttributeDisplayName))
                attribute.FriendlyName = claim.Properties[ClaimProperties.SamlAttributeDisplayName];

            return attribute;
        }

        /// <summary>
        /// Creates <see cref="Saml2AttributeStatement"/> from a <see cref="SecurityTokenDescriptor"/> and a <see cref="ClaimsIdentity"/>
        /// </summary>
        /// <remarks>This method may return null if the token descriptor does not contain any subject or the subject does not have any claims.
        /// </remarks>
        /// <param name="identity">The <see cref="ClaimsIdentity"/> that contains claims which will be converted to SAML Attributes.</param>
        /// <param name="tokenDescriptor">The <see cref="SecurityTokenDescriptor"/> that contains information on building the <see cref="Saml2AttributeStatement"/>.</param>
        /// <returns>A Saml2AttributeStatement.</returns>
        protected virtual Saml2AttributeStatement CreateAttributeStatement(ClaimsIdentity identity, SecurityTokenDescriptor tokenDescriptor)
        {
            if (identity == null)
                return null;

            // We treat everything else as an Attribute except the nameId claim, which is already processed
            // for saml2subject
            // AuthenticationInstant and AuthenticationType are not converted to Claims
            if (identity.Claims != null)
            {
                var attributes = new List<Saml2Attribute>();
                foreach (Claim claim in identity.Claims)
                {
                    if (claim != null && claim.Type != ClaimTypes.NameIdentifier)
                    {
                        switch (claim.Type)
                        {
                            case ClaimTypes.AuthenticationInstant:
                            case ClaimTypes.AuthenticationMethod:
                                break;
                            default:
                                attributes.Add(CreateAttribute(claim, tokenDescriptor));
                                break;
                        }
                    }
                }

                AddDelegateToAttributes(identity, attributes, tokenDescriptor);
                return new Saml2AttributeStatement(CollectAttributeValues(attributes));
            }

            return null;
        }

        /// <summary>
        /// Collects attributes with a common claim type, claim value type, and original issuer into a
        /// single attribute with multiple values.
        /// </summary>
        /// <param name="attributes">List of attributes generated from claims.</param>
        /// <returns>A <see cref="ICollection{T}"/> of <see cref="Saml2Attribute"/> with common attributes collected into value lists.</returns>
        protected virtual ICollection<Saml2Attribute> CollectAttributeValues(ICollection<Saml2Attribute> attributes)
        {
            var distinctAttributes = new Dictionary<Saml2AttributeKeyComparer.AttributeKey, Saml2Attribute>(attributes.Count, new Saml2AttributeKeyComparer());

            // Use unique attribute if name, value type, or issuer differ
            foreach (var saml2Attribute in attributes)
            {
                if (saml2Attribute != null)
                {
                    var attributeKey = new Saml2AttributeKeyComparer.AttributeKey(saml2Attribute);
                    if (distinctAttributes.ContainsKey(attributeKey))
                    {
                        foreach (string attributeValue in saml2Attribute.Values)
                            distinctAttributes[attributeKey].Values.Add(attributeValue);
                    }
                    else
                    {
                        distinctAttributes.Add(attributeKey, saml2Attribute);
                    }
                }
            }

            return distinctAttributes.Values;
        }

        /// <summary>
        /// Adds all the delegates associated with the subject into the attribute collection.
        /// </summary>
        /// <param name="identity">The delegate of this <see cref="ClaimsIdentity"/> will be serialized into a <see cref="Saml2Attribute"/>.</param>
        /// <param name="attributes">A <see cref="ICollection{T}"/> of <see cref="Saml2Attribute"/>.</param>
        /// <param name="tokenDescriptor">The <see cref="SecurityTokenDescriptor"/> that contains information on building the delegate.</param>
        protected virtual void AddDelegateToAttributes(ClaimsIdentity identity, ICollection<Saml2Attribute> attributes, SecurityTokenDescriptor tokenDescriptor)
        {
            if (identity == null)
                throw LogArgumentNullException(nameof(identity));

            if (tokenDescriptor == null)
                throw LogArgumentNullException(nameof(tokenDescriptor));

            if (identity.Actor == null)
                return;

            var actingAsAttributes = new List<Saml2Attribute>();
            foreach (Claim claim in identity.Actor.Claims)
            {
                if (claim != null)
                    actingAsAttributes.Add(CreateAttribute(claim, tokenDescriptor));
            }

            AddDelegateToAttributes(identity.Actor, actingAsAttributes, tokenDescriptor);

            ICollection<Saml2Attribute> collectedAttributes = CollectAttributeValues(actingAsAttributes);
            attributes.Add(CreateAttribute(new Claim(ClaimTypes.Actor, CreateXmlStringFromAttributes(collectedAttributes), ClaimValueTypes.String), tokenDescriptor));
        }

        /// <summary>
        /// Builds an XML formatted string from a collection of SAML attributes that represent the Actor. 
        /// </summary>
        /// <param name="attributes">An enumeration of Saml2Attributes.</param>
        /// <returns>A well-formed XML string.</returns>
        /// <remarks>The string is of the form "&lt;Actor&gt;&lt;Attribute name, ns&gt;&lt;AttributeValue&gt;...&lt;/AttributeValue&gt;, ...&lt;/Attribute&gt;...&lt;/Actor&gt;"</remarks>
        protected virtual string CreateXmlStringFromAttributes(IEnumerable<Saml2Attribute> attributes)
        {
            if (attributes == null)
                throw LogArgumentNullException(nameof(attributes));

            bool actorElementWritten = false;
            using (var ms = new MemoryStream())
            {
                using (var dictionaryWriter = XmlDictionaryWriter.CreateTextWriter(ms, Encoding.UTF8, false))
                {
                    foreach (var samlAttribute in attributes)
                    {
                        if (samlAttribute != null)
                        {
                            if (!actorElementWritten)
                            {
                                dictionaryWriter.WriteStartElement(Actor);
                                actorElementWritten = true;
                            }

                            Serializer.WriteAttribute(dictionaryWriter, samlAttribute);
                        }
                    }

                    if (actorElementWritten)
                        dictionaryWriter.WriteEndElement();

                    dictionaryWriter.Flush();
                }

                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        /// <summary>
        /// Creates an <see cref="IEnumerable{T}"/> of <see cref="Saml2Statement"/> to be included in the assertion.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Statements are not required in a SAML2 assertion. This method may
        /// return an empty collection.
        /// </para>
        /// </remarks>
        /// <param name="tokenDescriptor">The <see cref="SecurityTokenDescriptor"/> that contains information on creating the <see cref="Saml2Statement"/>.</param>
        /// <returns>An enumeration of Saml2Statements.</returns>
        protected virtual IEnumerable<Saml2Statement> CreateStatements(SecurityTokenDescriptor tokenDescriptor)
        {
            if (tokenDescriptor == null)
                throw LogArgumentNullException(nameof(tokenDescriptor));

            var statements = new Collection<Saml2Statement>();
            var attributeStatement = CreateAttributeStatement(tokenDescriptor.Subject, tokenDescriptor);
            if (attributeStatement != null)
                statements.Add(attributeStatement);

            // TODO - figure out how to set the AuthenticationInfo
            //var authenticationStatement = this.CreateAuthenticationStatement(tokenDescriptor.AuthenticationInfo, tokenDescriptor);
            //if (authenticationStatement != null)
            //    statements.Add(authenticationStatement);

            return statements;
        }

        /// <summary>
        /// Given an AuthenticationInformation object, this routine creates a Saml2AuthenticationStatement
        /// to be added to the Saml2Assertion that is produced by the factory.
        /// </summary>
        /// <param name="authInfo">
        /// An AuthenticationInformation object containing the state to be wrapped as a Saml2AuthenticationStatement
        /// object.
        /// </param>
        /// <param name="tokenDescriptor">The token descriptor.</param>
        /// <returns>
        /// The Saml2AuthenticationStatement to add to the assertion being created or null to ignore the AuthenticationInformation
        /// being wrapped as a statement.
        /// </returns>
        protected virtual Saml2AuthenticationStatement CreateAuthenticationStatement(AuthenticationInformation authInfo, SecurityTokenDescriptor tokenDescriptor)
        {
            if (tokenDescriptor == null)
                throw LogArgumentNullException(nameof(tokenDescriptor));

            if (tokenDescriptor.Subject == null)
                return null;

            Uri authenticationMethod = null;
            string authenticationInstant = null;

            // Search for an Authentication Claim.
            IEnumerable<Claim> claimCollection = from c in tokenDescriptor.Subject.Claims where c.Type == ClaimTypes.AuthenticationMethod select c;
            if (claimCollection.Count<Claim>() > 0)
            {
                // We support only one authentication statement and hence we just pick the first authentication type
                // claim found in the claim collection. Since the spec allows multiple Auth Statements, 
                // we do not throw an error.
                authenticationMethod = new Uri(claimCollection.First<Claim>().Value);
            }

            claimCollection = from c in tokenDescriptor.Subject.Claims where c.Type == ClaimTypes.AuthenticationInstant select c;

            if (claimCollection.Count<Claim>() > 0)
                authenticationInstant = claimCollection.First<Claim>().Value;

            if (authenticationMethod == null && authenticationInstant == null)
                return null;
            else if (authenticationMethod == null)
                throw LogExceptionMessage(new Saml2SecurityTokenException("ID4270, AuthenticationMethod, SAML2"));
            else if (authenticationInstant == null)
                throw LogExceptionMessage(new Saml2SecurityTokenException("ID4270, AuthenticationInstant, SAML2"));

            var authContext = new Saml2AuthenticationContext(authenticationMethod);
            var authInstantTime = DateTime.ParseExact(authenticationInstant, Saml2Constants.AcceptedDateTimeFormats, DateTimeFormatInfo.InvariantInfo, DateTimeStyles.None).ToUniversalTime();
            var authnStatement = new Saml2AuthenticationStatement(authContext, authInstantTime);

            if (authInfo != null)
            {
                if (!string.IsNullOrEmpty(authInfo.DnsName)
                    || !string.IsNullOrEmpty(authInfo.Address))
                    authnStatement.SubjectLocality
                        = new Saml2SubjectLocality(authInfo.Address, authInfo.DnsName);

                if (!string.IsNullOrEmpty(authInfo.Session))
                    authnStatement.SessionIndex = authInfo.Session;

                authnStatement.SessionNotOnOrAfter = authInfo.NotOnOrAfter;
            }

            return authnStatement;
        }

        /// <summary>
        /// Creates a SAML2 subject of the assertion.
        /// </summary>
        /// <param name="tokenDescriptor">The security token descriptor to create the subject.</param>
        /// <exception cref="ArgumentNullException">Thrown when 'tokenDescriptor' is null.</exception>
        /// <returns>A <see cref="Saml2Subject"/>.</returns>
        protected virtual Saml2Subject CreateSamlSubject(SecurityTokenDescriptor tokenDescriptor)
        {
            if (tokenDescriptor == null)
                throw LogArgumentNullException(nameof(tokenDescriptor));

            var saml2Subject = new Saml2Subject();

            // Look for name identifier claims
            string nameIdentifierClaim = null;
            string nameIdentifierFormat = null;
            string nameIdentifierNameQualifier = null;
            string nameIdentifierSpProviderId = null;
            string nameIdentifierSpNameQualifier = null;

            if (tokenDescriptor.Subject != null && tokenDescriptor.Subject.Claims != null)
            {
                foreach (var claim in tokenDescriptor.Subject.Claims)
                {
                    if (claim.Type == ClaimTypes.NameIdentifier)
                    {
                        // Do not allow multiple name identifier claim.
                        if (nameIdentifierClaim != null)
                            throw LogExceptionMessage(new Saml2SecurityTokenException("ID4139"));

                        nameIdentifierClaim = claim.Value;
                        if (claim.Properties.ContainsKey(ClaimProperties.SamlNameIdentifierFormat))
                            nameIdentifierFormat = claim.Properties[ClaimProperties.SamlNameIdentifierFormat];

                        if (claim.Properties.ContainsKey(ClaimProperties.SamlNameIdentifierNameQualifier))
                            nameIdentifierNameQualifier = claim.Properties[ClaimProperties.SamlNameIdentifierNameQualifier];

                        if (claim.Properties.ContainsKey(ClaimProperties.SamlNameIdentifierSPNameQualifier))
                            nameIdentifierSpNameQualifier = claim.Properties[ClaimProperties.SamlNameIdentifierSPNameQualifier];

                        if (claim.Properties.ContainsKey(ClaimProperties.SamlNameIdentifierSPProvidedId))
                            nameIdentifierSpProviderId = claim.Properties[ClaimProperties.SamlNameIdentifierSPProvidedId];
                    }
                }
            }

            if (nameIdentifierClaim != null)
            {
                var nameIdentifier = new Saml2NameIdentifier(nameIdentifierClaim);
                if (nameIdentifierFormat != null && Saml2Serializer.CanCreateValidUri(nameIdentifierFormat, UriKind.Absolute))
                    nameIdentifier.Format = new Uri(nameIdentifierFormat);

                nameIdentifier.NameQualifier = nameIdentifierNameQualifier;
                nameIdentifier.SPNameQualifier = nameIdentifierSpNameQualifier;
                nameIdentifier.SPProvidedId = nameIdentifierSpProviderId;
                saml2Subject.NameId = nameIdentifier;
            }

            // Add subject confirmation data
            Saml2SubjectConfirmation subjectConfirmation;
            //if (tokenDescriptor.Proof == null)
            {
                subjectConfirmation = new Saml2SubjectConfirmation(Saml2Constants.ConfirmationMethods.Bearer);
            }
            // TODO - proof of possession
            //else
            //{
            //    subjectConfirmation = new Saml2SubjectConfirmation(Saml2Constants.ConfirmationMethods.HolderOfKey, new Saml2SubjectConfirmationData());
            //    subjectConfirmation.SubjectConfirmationData.KeyIdentifiers.Add(tokenDescriptor.Proof.KeyIdentifier);
            //}

            saml2Subject.SubjectConfirmations.Add(subjectConfirmation);
            return saml2Subject;
        }

        /// <summary>
        /// Override this method to change the token encrypting credentials. 
        /// </summary>
        /// <param name="tokenDescriptor">Retrieve some scope encrypting credentials from the Scope object</param>
        /// <returns>the token encrypting credentials</returns>
        /// <exception cref="ArgumentNullException">When the given tokenDescriptor is null</exception>
        protected virtual EncryptingCredentials GetEncryptingCredentials(SecurityTokenDescriptor tokenDescriptor)
        {
            if (tokenDescriptor == null)
                throw LogArgumentNullException(nameof(tokenDescriptor));

            EncryptingCredentials encryptingCredentials = null;
            if (tokenDescriptor.EncryptingCredentials != null)
            {
                encryptingCredentials = tokenDescriptor.EncryptingCredentials;
                if (encryptingCredentials.Key is AsymmetricSecurityKey)
                    throw LogExceptionMessage(new Saml2SecurityTokenException("ID4178"));
            }

            return encryptingCredentials;
        }

        /// <summary>
        /// Gets the credentials for the signing the assertion.
        /// </summary>
        /// <remarks>
        /// <para>
        /// SAML2 assertions used as security tokens should be signed.
        /// </para>
        /// <para>
        /// The default implementation uses the 
        /// tokenDescriptor.Scope.SigningCredentials.
        /// </para>
        /// </remarks>
        /// <param name="tokenDescriptor">The token descriptor.</param>
        /// <returns>The signing credential.</returns>
        protected virtual SigningCredentials GetSigningCredentials(SecurityTokenDescriptor tokenDescriptor)
        {
            if (tokenDescriptor == null)
                throw LogArgumentNullException(nameof(tokenDescriptor));

            return tokenDescriptor.SigningCredentials;
        }

        /// <summary>
        /// Validates the Lifetime and Audience conditions.
        /// </summary>
        /// <param name="samlToken">a <see cref="Saml2SecurityToken"/> that contains the <see cref="Saml2Conditions"/>.</param>
        /// <param name="validationParameters"><see cref="TokenValidationParameters"/> required for validation.</param>
        /// <exception cref="ArgumentNullException">If 'securityToken' is null or empty.</exception>
        /// <exception cref="ArgumentNullException">If 'validationParameters' is null.</exception>
        /// <exception cref="ArgumentNullException">If 'securityToken.Assertion' is null.</exception>
        /// <exception cref="SecurityTokenValidationException">if the Condition 'OneTimeUse' is specified. Requires overriding.</exception>
        /// <exception cref="SecurityTokenValidationException">if the Condition 'ProxyRestriction' is specified. Requires overriding.</exception>
        protected virtual void ValidateConditions(Saml2SecurityToken samlToken, TokenValidationParameters validationParameters)
        {
            if (samlToken == null)
                throw LogArgumentNullException(nameof(samlToken));

            if (validationParameters == null)
                throw LogArgumentNullException(nameof(validationParameters));

            if (samlToken.Assertion == null)
                throw LogArgumentNullException(nameof(samlToken.Assertion));

            if (samlToken.Assertion.Conditions != null)
            {
                var utcNow = DateTime.UtcNow;
                Validators.ValidateLifetime(samlToken.Assertion.Conditions.NotBefore, samlToken.Assertion.Conditions.NotOnOrAfter, samlToken, validationParameters);

                if (samlToken.Assertion.Conditions.OneTimeUse)
                    throw LogExceptionMessage(new SecurityTokenValidationException(LogMessages.IDX10510));

                if (samlToken.Assertion.Conditions.ProxyRestriction != null)
                    throw LogExceptionMessage(new SecurityTokenValidationException(LogMessages.IDX10511));
            }

            // TODO - concat all the audiences together
            foreach (var audienceRestriction in samlToken.Assertion.Conditions.AudienceRestrictions)
            {
                if (validationParameters.AudienceValidator != null)
                    validationParameters.AudienceValidator(audienceRestriction.Audiences, samlToken, validationParameters);
                else
                    Validators.ValidateAudience(audienceRestriction.Audiences, samlToken, validationParameters);
            }
        }

        // TODO - not sure what this is about
        ///// <summary>
        ///// Returns the Saml2 AuthenticationContext matching a normalized value.
        ///// </summary>
        ///// <param name="normalizedAuthenticationType">Normalized value.</param>
        ///// <returns>A string that represents the denormalized authentication type used to obtain the token.</returns>
        //protected virtual string DenormalizeAuthenticationType(string normalizedAuthenticationType)
        //{
        //    return AuthenticationTypeMaps.Denormalize(normalizedAuthenticationType, AuthenticationTypeMaps.Saml2);
        //}

        // TODO - do we need to normalize ?
        ///// <summary>
        ///// Returns the normalized value matching a SAML2 AuthenticationContext class reference.
        ///// </summary>
        ///// <param name="saml2AuthenticationContextClassReference">A string representing the <see cref="Saml2Constants.AuthenticationContextClasses"/></param>
        ///// <returns>Normalized value.</returns>
        //protected virtual string NormalizeAuthenticationContextClassReference(string saml2AuthenticationContextClassReference)
        //{
        //    return AuthenticationTypeMaps.Normalize(saml2AuthenticationContextClassReference, AuthenticationTypeMaps.Saml2);
        //}


        /// <summary>
        /// This method gets called when a special type of Saml2Attribute is detected. The Saml2Attribute passed in 
        /// wraps a Saml2Attribute that contains a collection of AttributeValues, each of which will get mapped to a 
        /// claim.  All of the claims will be returned in an ClaimsIdentity with the specified issuer.
        /// </summary>
        /// <param name="attribute">The <see cref="Saml2Attribute"/> to use.</param>
        /// <param name="identity">The <see cref="ClaimsIdentity"/> that is the subject of this token.</param>
        /// <param name="issuer">The issuer of the claim.</param>
        /// <exception cref="InvalidOperationException">Will be thrown if the Saml2Attribute does not contain any 
        /// valid Saml2AttributeValues.
        /// </exception>
        protected virtual void SetDelegateFromAttribute(Saml2Attribute attribute, ClaimsIdentity identity, string issuer)
        {
            // bail here; nothing to add.
            if (identity == null || attribute == null || attribute.Values == null || attribute.Values.Count < 1)
                return;

            Saml2Attribute actingAsAttribute = null;
            var claims = new Collection<Claim>();
            foreach (string attributeValue in attribute.Values)
            {
                if (attributeValue != null)
                {
                    using (var dictionaryReader = XmlDictionaryReader.CreateTextReader(Encoding.UTF8.GetBytes(attributeValue), XmlDictionaryReaderQuotas.Max))
                    {
                        dictionaryReader.MoveToContent();
                        dictionaryReader.ReadStartElement(Actor);

                        while (dictionaryReader.IsStartElement(Saml2Constants.Elements.Attribute))
                        {
                            var innerAttribute = Serializer.ReadAttribute(dictionaryReader);
                            if (innerAttribute != null)
                            {
                                if (innerAttribute.Name == ClaimTypes.Actor)
                                {
                                    // In this case, we have two delegates acting as an identity: we do not allow this.
                                    if (actingAsAttribute != null)
                                        throw LogExceptionMessage(new Saml2SecurityTokenException(LogMessages.IDX11142));

                                    actingAsAttribute = innerAttribute;
                                }
                                else
                                {
                                    string originalIssuer = innerAttribute.OriginalIssuer;
                                    foreach (var attr in innerAttribute.Values)
                                    {
                                        Claim claim = null;
                                        if (string.IsNullOrEmpty(originalIssuer))
                                            claim = new Claim(innerAttribute.Name, attr, innerAttribute.AttributeValueXsiType, issuer);
                                        else
                                            claim = new Claim(innerAttribute.Name, attr, innerAttribute.AttributeValueXsiType, issuer, originalIssuer);

                                        if (innerAttribute.NameFormat != null)
                                            claim.Properties[ClaimProperties.SamlAttributeNameFormat] = innerAttribute.NameFormat.OriginalString;

                                        if (innerAttribute.FriendlyName != null)
                                            claim.Properties[ClaimProperties.SamlAttributeDisplayName] = innerAttribute.FriendlyName;

                                        claims.Add(claim);
                                    }
                                }
                            }
                        }

                        dictionaryReader.ReadEndElement(); // Actor
                    }
                }
            }

            // TODO - what should the authenticationType be, call tokenvalidationParameters.CreateClaimsIdentity
            identity.Actor = new ClaimsIdentity(claims);
            SetDelegateFromAttribute(actingAsAttribute, identity.Actor, issuer);
        }

        /// <summary>
        /// Processes all statements and adds claims to the identity.
        /// </summary>
        /// <param name="statements">A collection of Saml2Statement.</param>
        /// <param name="identity">The <see cref="ClaimsIdentity"/>.</param>
        /// <param name="issuer">The issuer.</param>
        protected virtual void ProcessStatements(ICollection<Saml2Statement> statements, ClaimsIdentity identity, string issuer)
        {
            var authnStatements = new Collection<Saml2AuthenticationStatement>();
            foreach (var statement in statements)
            {
                if (statement is Saml2AttributeStatement attrStatement)
                    ProcessAttributeStatement(attrStatement, identity, issuer);
                else if (statement is Saml2AuthenticationStatement authnStatement)
                    authnStatements.Add(authnStatement);
                else if (statement is Saml2AuthorizationDecisionStatement authzStatement)
                    ProcessAuthorizationDecisionStatement(authzStatement, identity, issuer);

                // We don't process custom statements. Just fall through.
            }

            foreach (var authStatement in authnStatements)
            {
                if (authStatement != null)
                    ProcessAuthenticationStatement(authStatement, identity, issuer);
            }
        }

        /// <summary>
        /// Adds claims from the <see cref="Saml2Subject"/> into the <see cref="ClaimsIdentity"/>.
        /// </summary>
        /// <param name="subject">The <see cref="Saml2Subject"/>.</param>
        /// <param name="identity">The <see cref="ClaimsIdentity"/>.</param>
        /// <param name="issuer">The issuer.</param>
        protected virtual void ProcessSubject(Saml2Subject subject, ClaimsIdentity identity, string issuer)
        {
            if (subject == null)
                throw LogArgumentNullException(nameof(subject));

            if (identity == null)
                throw LogArgumentNullException(nameof(identity));

            var nameId = subject.NameId;
            if (nameId != null)
            {
                var claim = new Claim(ClaimTypes.NameIdentifier, nameId.Value, ClaimValueTypes.String, issuer);
                if (nameId.Format != null)
                    claim.Properties[ClaimProperties.SamlNameIdentifierFormat] = nameId.Format.OriginalString;

                if (nameId.NameQualifier != null)
                    claim.Properties[ClaimProperties.SamlNameIdentifierNameQualifier] = nameId.NameQualifier;

                if (nameId.SPNameQualifier != null)
                    claim.Properties[ClaimProperties.SamlNameIdentifierSPNameQualifier] = nameId.SPNameQualifier;

                if (nameId.SPProvidedId != null)
                    claim.Properties[ClaimProperties.SamlNameIdentifierSPProvidedId] = nameId.SPProvidedId;

                identity.AddClaim(claim);
            }
        }

        /// <summary>
        /// Creates claims from a <see cref="Saml2AttributeStatement"/>.
        /// </summary>
        /// <param name="statement">The <see cref="Saml2AttributeStatement"/>.</param>
        /// <param name="identity">The subject.</param>
        /// <param name="issuer">The issuer.</param>
        protected virtual void ProcessAttributeStatement(Saml2AttributeStatement statement, ClaimsIdentity identity, string issuer)
        {
            if (statement == null)
                throw LogArgumentNullException(nameof(statement));

            if (identity == null)
                throw LogArgumentNullException(nameof(identity));

            foreach (var attribute in statement.Attributes)
            {
                if (StringComparer.Ordinal.Equals(attribute.Name, ClaimTypes.Actor))
                {
                    // TODO - should we support nested Actors?
                    if (identity.Actor != null)
                        throw LogExceptionMessage(new Saml2SecurityTokenException(LogMessages.IDX10512));

                    SetDelegateFromAttribute(attribute, identity, issuer);
                }
                else
                {
                    // each value has same issuer
                    string originalIssuer = attribute.OriginalIssuer ?? issuer;
                    foreach (string value in attribute.Values)
                    {
                        if (value != null)
                        {
                            var claim = new Claim(attribute.Name, value, attribute.AttributeValueXsiType, issuer, originalIssuer);
                            if (attribute.NameFormat != null)
                                claim.Properties[ClaimProperties.SamlAttributeNameFormat] = attribute.NameFormat.OriginalString;

                            if (attribute.FriendlyName != null)
                                claim.Properties[ClaimProperties.SamlAttributeDisplayName] = attribute.FriendlyName;

                            identity.AddClaim(claim);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Creates claims from a <see cref="Saml2AuthenticationStatement"/>.
        /// </summary>
        /// <param name="statement">The <see cref="Saml2AuthenticationStatement"/>.</param>
        /// <param name="identity">The subject.</param>
        /// <param name="issuer">The issuer.</param>
        protected virtual void ProcessAuthenticationStatement(Saml2AuthenticationStatement statement, ClaimsIdentity identity, string issuer)
        {
            if (identity == null)
                throw LogArgumentNullException(nameof(identity));

            if (statement.AuthenticationContext.DeclarationReference != null)
                throw LogExceptionMessage(new Saml2SecurityTokenException(LogMessages.IDX14001));

            if (statement.AuthenticationContext.ClassReference != null)
            {
                identity.AddClaim(
                    new Claim(ClaimTypes.AuthenticationMethod,
                              statement.AuthenticationContext.ClassReference.OriginalString,
                              ClaimValueTypes.String,
                              issuer));
            }

            identity.AddClaim(new Claim(ClaimTypes.AuthenticationInstant, XmlConvert.ToString(statement.AuthenticationInstant.ToUniversalTime(), Saml2Constants.GeneratedDateTimeFormat), ClaimValueTypes.DateTime, issuer));
        }

        /// <summary>
        /// Creates claims from a <see cref="Saml2AuthorizationDecisionStatement"/>.
        /// </summary>
        /// <param name="statement">The <see cref="Saml2AuthorizationDecisionStatement"/>.</param>
        /// <param name="identity">The <see cref="ClaimsIdentity"/>.</param>
        /// <param name="issuer">The issuer.</param>
        /// <remarks>Provided for extensibility. By default no claims are added.</remarks>
        protected virtual void ProcessAuthorizationDecisionStatement(Saml2AuthorizationDecisionStatement statement, ClaimsIdentity identity, string issuer)
        {
        }

        /// <summary>
        /// Creates claims from a Saml2 token.
        /// </summary>
        /// <param name="samlToken">The <see cref="Saml2SecurityToken"/>.</param>
        /// <param name="issuer">The value to set <see cref="Claim.Issuer"/></param>
        /// <param name="validationParameters">creates the <see cref="ClaimsIdentity"/> using <see cref="TokenValidationParameters.CreateClaimsIdentity(SecurityToken, string)"/>.</param>
        /// <returns>A <see cref="ClaimsIdentity"/> with claims from the saml statements.</returns>
        protected virtual ClaimsIdentity CreateClaimsIdentity(Saml2SecurityToken samlToken, string issuer, TokenValidationParameters validationParameters)
        {
            if (samlToken == null)
                throw LogArgumentNullException(nameof(samlToken));

            var assertion = samlToken.Assertion;
            if (assertion == null)
                throw LogArgumentNullException(LogMessages.IDX11110);

            var actualIssuer = issuer;
            if (string.IsNullOrWhiteSpace(issuer))
            {
                IdentityModelEventSource.Logger.WriteVerbose(TokenLogMessages.IDX10244, ClaimsIdentity.DefaultIssuer);
                actualIssuer = ClaimsIdentity.DefaultIssuer;
            }

            var identity = validationParameters.CreateClaimsIdentity(samlToken, actualIssuer);
            ProcessSubject(assertion.Subject, identity, actualIssuer);
            ProcessStatements(assertion.Statements, identity, actualIssuer);

            return identity;
        }

        /// <summary>
        /// Determines if the audience found in a <see cref="Saml2SecurityToken"/> is valid.
        /// </summary>
        /// <param name="audiences">The audiences found in the <see cref="Saml2SecurityToken"/></param>
        /// <param name="securityToken">The <see cref="Saml2SecurityToken"/> that is being validated.</param>
        /// <param name="validationParameters"><see cref="TokenValidationParameters"/> required for validation.</param>
        /// <remarks><see cref="Validators.ValidateAudience(IEnumerable{string}, SecurityToken, TokenValidationParameters)"/> for additional details.</remarks>
        protected virtual void ValidateAudience(IEnumerable<string> audiences, SecurityToken securityToken, TokenValidationParameters validationParameters)
        {
            Validators.ValidateAudience(audiences, securityToken, validationParameters);
        }

        /// <summary>
        /// Determines if the issuer found in a <see cref="Saml2SecurityToken"/> is valid.
        /// </summary>
        /// <param name="issuer">The issuer to validate</param>
        /// <param name="securityToken">The <see cref="Saml2SecurityToken"/> that is being validated.</param>
        /// <param name="validationParameters"><see cref="TokenValidationParameters"/> required for validation.</param>
        /// <returns>The issuer to use when creating the <see cref="Claim"/>(s) in the <see cref="ClaimsIdentity"/>.</returns>
        /// <remarks><see cref="Validators.ValidateIssuer"/> for additional details.</remarks>
        protected virtual string ValidateIssuer(string issuer, SecurityToken securityToken, TokenValidationParameters validationParameters)
        {
            return Validators.ValidateIssuer(issuer, securityToken, validationParameters);
        }

        /// <summary>
        /// Writes a Saml2 Token using the XmlWriter.
        /// </summary>
        /// <param name="writer">A <see cref="XmlWriter"/> to serialize the <see cref="SecurityToken"/>.</param>
        /// <param name="token">The <see cref="SecurityToken"/> to serialize.</param>
        /// <exception cref="ArgumentNullException">If 'writer' is null.</exception>
        /// <exception cref="ArgumentNullException">If 'token' is null.</exception>
        /// <exception cref="Saml2SecurityTokenWriteException">If 'token' is not a <see cref="Saml2SecurityToken"/>.</exception>
        public override void WriteToken(XmlWriter writer, SecurityToken token)
        {
            if (writer == null)
                throw LogArgumentNullException(nameof(writer));

            if (token == null)
                throw LogArgumentNullException(nameof(token));

            var samlToken = token as Saml2SecurityToken;
            if (samlToken == null)
                throw Saml2Serializer.LogWriteException(LogMessages.IDX11150, token.GetType());

            Serializer.WriteAssertion(writer, samlToken.Assertion);
        }
    }
}
